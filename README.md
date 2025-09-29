# kooyoseb itchio 카카오톡 알림기 봇

## 환경변수 설정
- ITCH_API_KEY : itch.io API 키
- ITCH_USERNAME : itch.io 사용자명 (예: kooyoseb)
- POLL_INTERVAL_SEC : 폴링 주기 (기본 60)
- KAKAO_SKILL_SECRET : (선택) 오픈빌더 서버 연결 검증용 시크릿
- KAKAO_BIZ_ENABLED : true/false (비즈메시지 사용 여부)
- KAKAO_BIZ_TOKEN : (옵션) 비즈메시지 API 토큰

## 빌드 & 실행
```bash
dotnet run
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true
```

## Render 배포
1. GitHub에 업로드 후 Render Web Service 생성
2. Dockerfile 자동 인식됨
3. Environment Variables 탭에서 위 환경변수 등록
4. 배포 후 `https://<project>.onrender.com/kakao/skill` 을 오픈빌더 서버 연결 URL로 등록
