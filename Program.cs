using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// ======= 고정 설정 =======
var cfg = new BotConfig(
    ItchApiKey: "Q0ipCNsGZnvaEimqPqzW2vVpUH5dZqp4jFQFFCcF",
    ItchUserName: "kooyoseb",
    PollIntervalSec: 29
);

// ======= DB 초기화 =======
Directory.CreateDirectory("data");
using (var con = new SqliteConnection("Data Source=data/bot.db"))
{
    con.Open();
    using var cmd1 = con.CreateCommand();
    cmd1.CommandText = """
    CREATE TABLE IF NOT EXISTS Subscribers(
        user_id TEXT PRIMARY KEY,
        user_name TEXT,
        created_at TEXT
    );
    """;
    cmd1.ExecuteNonQuery();

    using var cmd2 = con.CreateCommand();
    cmd2.CommandText = """
    CREATE TABLE IF NOT EXISTS EventState(
        key TEXT PRIMARY KEY,
        value TEXT
    );
    """;
    cmd2.ExecuteNonQuery();

    using var cmd3 = con.CreateCommand();
    cmd3.CommandText = "INSERT OR IGNORE INTO EventState(key,value) VALUES('last_check','1970-01-01T00:00:00Z')";
    cmd3.ExecuteNonQuery();
}

// ======= 서비스 등록 =======
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<ItchClient>();
builder.Services.AddSingleton<DbRepo>();
builder.Services.AddHostedService<ItchPollingWorker>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { ok = true, service = "kooyoseb-itch-kakao-bot" }));

// ======= 카카오 오픈빌더 Webhook =======
app.MapPost("/kakao/skill", async (HttpRequest req, DbRepo db, ItchClient itch) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    var skillReq = JsonSerializer.Deserialize<KakaoSkillRequest>(raw);

    var userId = skillReq?.UserRequest?.User?.Id ?? "unknown";
    var utter = (skillReq?.UserRequest?.Utterance ?? "").Trim();

    string reply;
    if (utter.StartsWith("/구독"))
    {
        await db.AddSubscriber(userId, skillReq?.UserRequest?.User?.Properties?.Nickname ?? "User");
        reply = "구독 완료!";
    }
    else if (utter.StartsWith("/해지"))
    {
        await db.RemoveSubscriber(userId);
        reply = "구독 해지 완료!";
    }
    else if (utter.StartsWith("/최근"))
    {
        var items = await itch.FetchRecentUpdates(5);
        reply = FormatItems(items);
    }
    else
    {
        reply = "명령어 안내:\n/구독 /해지 /최근";
    }

    return Results.Json(new KakaoSkillResponse
    {
        Version = "2.0",
        Template = new Template
        {
            Outputs = new[] { new Output { SimpleText = new SimpleText { Text = reply } } }
        }
    });
});

app.Run();

// ======= Helper 함수 =======
static string FormatItems(List<ItchItem> items)
{
    if (items.Count == 0) return "최근 업데이트 없음.";
    var sb = new StringBuilder();
    foreach (var i in items)
    {
        sb.AppendLine($"[{i.Type}] {i.Title}");
        sb.AppendLine($" - {i.Url}");
        sb.AppendLine($" - {i.PublishedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
    }
    return sb.ToString();
}

// ======= Config/Models =======
public record BotConfig(string ItchApiKey, string ItchUserName, int PollIntervalSec);


public class KakaoSkillRequest
{
    [JsonPropertyName("userRequest")] public UserRequest? UserRequest { get; set; }
}
public class UserRequest
{
    [JsonPropertyName("user")] public KakaoUser? User { get; set; }
    [JsonPropertyName("utterance")] public string? Utterance { get; set; }
}
public class KakaoUser
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("properties")] public KakaoUserProps? Properties { get; set; }
}
public class KakaoUserProps
{
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
}

public class KakaoSkillResponse
{
    [JsonPropertyName("version")] public string Version { get; set; } = "2.0";
    [JsonPropertyName("template")] public Template Template { get; set; } = new();
}
public class Template
{
    [JsonPropertyName("outputs")] public Output[] Outputs { get; set; } = Array.Empty<Output>();
}
public class Output
{
    [JsonPropertyName("simpleText")] public SimpleText? SimpleText { get; set; }
}
public class SimpleText { [JsonPropertyName("text")] public string Text { get; set; } = ""; }

// ======= DB Repo =======
public class DbRepo
{
    private readonly string _cs = "Data Source=data/bot.db";
    public async Task AddSubscriber(string userId, string userName)
    {
        using var con = new SqliteConnection(_cs);
        await con.OpenAsync();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO Subscribers(user_id,user_name,created_at) VALUES(@id,@name,@ts)";
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@name", userName);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task RemoveSubscriber(string userId)
    {
        using var con = new SqliteConnection(_cs);
        await con.OpenAsync();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Subscribers WHERE user_id=@id";
        cmd.Parameters.AddWithValue("@id", userId);
        await cmd.ExecuteNonQueryAsync();
    }
}

// ======= Itch.io API =======
public class ItchClient
{
    private readonly HttpClient _http = new();
    private readonly BotConfig _cfg;
    public ItchClient(BotConfig cfg)
    {
        _cfg = cfg;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ItchApiKey);
    }

    public async Task<List<ItchItem>> FetchRecentUpdates(int limit = 5)
    {
        var items = new List<ItchItem>();
        var me = await GetJson($"https://itch.io/api/1/key/me");
        var games = await GetJson($"https://itch.io/api/1/key/my-games");
        if (games.RootElement.TryGetProperty("games", out var arr))
        {
            foreach (var g in arr.EnumerateArray())
            {
                var title = g.GetProperty("title").GetString() ?? "Untitled";
                var url = g.GetProperty("url").GetString() ?? "";
                var upd = g.TryGetProperty("published_at", out var p) ? p.GetString() : null;
                DateTime.TryParse(upd, out var pub);

                items.Add(new ItchItem
                {
                    Type = "Game",
                    Title = title,
                    Url = url,
                    PublishedAt = pub == default ? DateTime.UtcNow : pub.ToUniversalTime()
                });
            }
        }
        return items.OrderByDescending(x => x.PublishedAt).Take(limit).ToList();
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        using var res = await _http.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var bytes = await res.Content.ReadAsByteArrayAsync();
        return JsonDocument.Parse(bytes);
    }
}

public class ItchItem
{
    public string Type { get; set; } = "Update";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

// ======= Background Worker =======
public class ItchPollingWorker : BackgroundService
{
    private readonly ILogger<ItchPollingWorker> _log;
    private readonly BotConfig _cfg;
    private readonly DbRepo _db;
    private readonly ItchClient _itch;

    public ItchPollingWorker(ILogger<ItchPollingWorker> log, BotConfig cfg, DbRepo db, ItchClient itch)
    {
        _log = log; _cfg = cfg; _db = db; _itch = itch;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Polling started");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var news = await _itch.FetchRecentUpdates(3);
                if (news.Count > 0)
                {
                    _log.LogInformation("새 소식 {cnt}개 발견", news.Count);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Polling error");
            }
            await Task.Delay(TimeSpan.FromSeconds(_cfg.PollIntervalSec), stoppingToken);
        }
    }
}
