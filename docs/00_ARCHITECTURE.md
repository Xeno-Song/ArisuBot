# ArisuBot — 아키텍처

## 개요

Discord chatbot framework powered by LLM. C# / Discord.Net. Gemini default, provider-swappable. MongoDB for persistence. Docker single-container deployment.

---

## 기술 스택

| 레이어 | 기술 |
|--------|------|
| Language | C# / .NET |
| Discord | Discord.Net (official) |
| LLM Default | Google Gemini |
| Database | MongoDB |
| File Logging | Log4Net (rolling) |
| Deployment | Docker (single container) |

---

## 프로젝트 구조

```
ArisuBot/
├── src/
│   ├── ArisuBot.Core/              # Domain — interfaces, models, services
│   │   ├── Interfaces/
│   │   │   ├── ILLMProvider.cs
│   │   │   ├── IConversationRepository.cs
│   │   │   └── IAIMessageLogger.cs
│   │   ├── Models/
│   │   │   ├── ChatMessage.cs
│   │   │   ├── ConversationContext.cs
│   │   │   └── LLMResponse.cs
│   │   └── Services/
│   │       └── ConversationService.cs
│   │
│   ├── ArisuBot.LLM/               # LLM provider implementations
│   │   ├── Gemini/
│   │   │   └── GeminiProvider.cs
│   │   └── LLMProviderFactory.cs
│   │
│   ├── ArisuBot.Discord/           # Discord.Net integration
│   │   ├── BotClient.cs
│   │   ├── Commands/               # Slash commands
│   │   │   └── PingCommand.cs      # (예시)
│   │   └── Handlers/
│   │       ├── MessageHandler.cs   # 메시지 리스너
│   │       └── SlashCommandHandler.cs
│   │
│   ├── ArisuBot.Infrastructure/    # MongoDB, Log4Net 구현체
│   │   ├── MongoDB/
│   │   │   ├── ConversationRepository.cs
│   │   │   └── AIMessageLogger.cs
│   │   └── Logging/
│   │       └── Log4NetLogger.cs
│   │
│   └── ArisuBot.Host/              # Entry point, DI 구성, Config
│       ├── Program.cs
│       ├── appsettings.json
│       └── appsettings.Local.json  # .gitignore 등록, READ 금지
│
├── tests/
│   ├── ArisuBot.Tests.Unit/        # 단위 테스트 (90% 커버리지 목표)
│   └── ArisuBot.Tests.Integration/ # 통합 테스트 (60% 커버리지 목표)
│
├── docs/                           # 프로젝트 문서
├── Dockerfile
└── .gitignore
```

---

## 설정 파일

### `appsettings.json` (non-secret, 커밋 가능)

```json
{
  "Discord": {
    "Persona": "You are a helpful assistant named Arisu.",
    "MessageListener": {
      "ChannelIds": [],
      "RespondToMentions": true
    }
  },
  "LLM": {
    "Provider": "Gemini",
    "MaxTokens": 2048,
    "Temperature": 0.7
  },
  "MongoDB": {
    "DatabaseName": "arisu_bot"
  },
  "Memory": {
    "UserContextMaxMessages": 20,
    "ChannelContextMaxMessages": 50
  },
  "Logging": {
    "Log4Net": {
      "RollingLogPath": "logs/arisu-bot.log",
      "MaxFileSize": "10MB",
      "MaxBackups": 10
    }
  }
}
```

### `appsettings.Local.json` (secret, **절대 커밋 금지**)

```json
{
  "Discord": {
    "Token": "DISCORD_BOT_TOKEN"
  },
  "LLM": {
    "Gemini": {
      "ApiKey": "GEMINI_API_KEY"
    }
  },
  "MongoDB": {
    "ConnectionString": "mongodb://..."
  }
}
```

---

## 핵심 인터페이스

```csharp
public interface ILLMProvider
{
    string ProviderName { get; }
    Task<LLMResponse> GenerateAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken ct = default);
}

public interface IConversationRepository
{
    Task<ConversationContext> GetUserContextAsync(ulong userId);
    Task<ConversationContext> GetChannelContextAsync(ulong channelId);
    Task SaveMessageAsync(ConversationContext context, ChatMessage message);
    Task ClearContextAsync(ConversationContext context);
}

public interface IAIMessageLogger
{
    Task LogAsync(ulong guildId, ulong channelId, ulong userId,
                  string userMessage, string botResponse, string provider);
}
```

---

## 메시지 처리 흐름

`ConversationService`는 컨텍스트 관리만 담당한다. LLM 호출과 로깅은 Handler(오케스트레이터)가 직접 수행한다.

> **구현 상태**: `MessageHandler`의 LLM 연동 (ConversationService + ILLMProvider 호출) 미구현. Discord 레이어 구조만 완성. LLM Provider 구현 완료 후 연동 예정.

```
Discord Event
     │
     ▼
[MessageHandler / SlashCommandHandler]  ← 오케스트레이터 역할
     │
     ├─ 채널 Config 확인 (ShouldRespond)
     │
     ├─ [미구현] ConversationService.GetContextAsync()
     ├─ [미구현] ConversationService.BuildMessageList()
     │
     ├─ [미구현] ILLMProvider.GenerateAsync()
     │
     ├─ [미구현] ConversationService.AppendMessageAsync() × 2
     ├─ [미구현] IAIMessageLogger.LogAsync()
     │
     ▼
Discord 채널에 응답 전송
```

---

## LLM Provider 교체

### 인터페이스 확장

```csharp
// 새 Provider 추가 시 ILLMProvider 구현만으로 충분
public class OpenAIProvider : ILLMProvider { ... }
public class OllamaProvider : ILLMProvider { ... }
```

### Config 기반 런타임 선택

```csharp
// LLMProviderFactory
services.AddSingleton<ILLMProvider>(sp =>
{
    var providerName = config["LLM:Provider"];
    return providerName switch
    {
        "Gemini" => sp.GetRequiredService<GeminiProvider>(),
        "OpenAI" => sp.GetRequiredService<OpenAIProvider>(),
        _ => throw new InvalidOperationException($"Unknown provider: {providerName}")
    };
});
```

---

## MongoDB Collections

| Collection | 용도 |
|-----------|------|
| `conversation_contexts` | 유저/채널별 대화 히스토리 |
| `ai_message_logs` | 전체 AI 메시지 감사 로그 |

### `conversation_contexts` Document

```json
{
  "_id": "...",
  "type": "user",
  "targetId": "123456789",
  "messages": [
    { "role": "user", "content": "안녕", "timestamp": "..." },
    { "role": "assistant", "content": "안녕하세요!", "timestamp": "..." }
  ],
  "updatedAt": "..."
}
```

### `ai_message_logs` Document

```json
{
  "_id": "...",
  "guildId": "...",
  "channelId": "...",
  "userId": "...",
  "userMessage": "...",
  "botResponse": "...",
  "provider": "Gemini",
  "timestamp": "..."
}
```

---

## 로깅 전략

| 대상 | 저장소 | 형식 |
|------|--------|------|
| 앱 실행 로그 (Info/Warn/Error) | 파일 (Log4Net Rolling) | 텍스트 |
| AI 메시지 입출력 | MongoDB `ai_message_logs` | Document |
| Discord 이벤트 | 파일 (Log4Net) | 텍스트 |

---

## Dockerfile (단일 컨테이너)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY . .
RUN dotnet publish src/ArisuBot.Host/ArisuBot.Host.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
VOLUME ["/app/logs", "/app/config"]
ENTRYPOINT ["dotnet", "ArisuBot.Host.dll"]
```

`appsettings.Local.json`은 볼륨 마운트 또는 환경변수로 주입.

---

## 확장 계획 (초기 버전 이후)

- [ ] 플러그인 시스템 (동적 어셈블리 로딩)
- [ ] 외부 설정 기반 커맨드 등록
- [ ] 추가 LLM Provider (OpenAI, Claude, Ollama)
- [ ] 웹 관리 UI (Bot 설정, 로그 조회)
