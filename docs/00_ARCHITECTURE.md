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
│   │   │   ├── ISemanticMemoryRepository.cs
│   │   │   ├── ISemanticMemoryService.cs
│   │   │   └── IAIMessageLogger.cs
│   │   ├── Models/
│   │   │   ├── ChatMessage.cs
│   │   │   ├── ConversationContext.cs
│   │   │   ├── SemanticMemoryData.cs
│   │   │   ├── UserSemanticMemory.cs
│   │   │   └── LLMResponse.cs
│   │   ├── Options/
│   │   │   └── SemanticMemoryOptions.cs
│   │   └── Services/
│   │       ├── ConversationService.cs
│   │       └── SemanticMemoryService.cs
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
│   │   │   ├── SemanticMemoryRepository.cs
│   │   │   ├── AIMessageLogger.cs
│   │   │   └── Documents/
│   │   │       ├── ConversationDocument.cs
│   │   │       └── SemanticMemoryDocument.cs
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

### 일반 텍스트 응답 흐름

```
Discord Event
     │
     ▼
[MessageHandler]  ← 오케스트레이터 역할
     │
     ├─ 채널 Config 확인 (ShouldRespond) / DM whitelist 확인
     ├─ ConversationService.GetContextAsync()
     ├─ ConversationService.BuildMessageList()
     │
     ├─ ILLMProvider.GenerateAsync(messages, tools, toolContext)
     │
     ├─ ConversationService.AppendMessageAsync() × 2
     ├─ IAIMessageLogger.LogAsync()
     │
     ▼
Discord 채널에 응답 전송 (2000자 청크 분할)
```

### Tool Use (함수 호출) 흐름

Guild 채널 메시지 시 tools + toolContext를 GenerateAsync에 전달한다. DM은 toolContext=null이므로 툴 비활성화.

```
MessageHandler
  └─ GeminiProvider.GenerateAsync(messages, tools, toolContext)
       │
       └─ [루프: 최대 MaxToolIterations 회]
             ├─ StreamAsync(model, contents, config{Tools}) → 스트리밍 응답 누적
             ├─ FunctionCalls 없음 → LLMResponse 반환 (루프 종료)
             └─ FunctionCalls 있음
                  ├─ contents에 model FunctionCall 추가
                  ├─ ILLMTool.ExecuteAsync(args, {guildId, channelId}) → 결과 문자열
                  ├─ contents에 user FunctionResponse 추가
                  └─ 다음 반복 (재호출)
```

### 구현된 LLM 툴

| 툴 이름 | 클래스 | 설명 |
|---------|--------|------|
| `discord_timeout_user` | `TimeoutUserTool` | 유저 타임아웃 적용 |
| `discord_list_channel_users` | `ListChannelUsersTool` | 채널 접근 권한 멤버 목록 |

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

| Collection | 용도 | 사용 위치 |
|-----------|------|----------|
| `conversation_contexts` | 유저/채널별 대화 히스토리 + 토큰 사용량 | `ConversationRepository` |
| `ai_message_logs` | 전체 AI 메시지 감사 로그 | `AIMessageLogger` |
| `error_logs` | LLM/Tool 에러 로그 | `ErrorLogger` |
| `semantic_memory` | 유저별 session별 semantic memory (traits/episodic) | `SemanticMemoryRepository` |

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

## Semantic Memory

### 개요

Compaction과 완전히 분리된 독립 서비스. 대화 종료 후 유저별 맞춤 정보를 추출하여 영구 저장하고, 이후 세션에서 첫 메시지 시 LLM context에 주입한다.

### 데이터 모델

```csharp
// per-session document (MongoDB)
public class UserSemanticMemory {
    public string Id { get; set; }              // MongoDB ObjectId (InsertOne 후 채워짐)
    public ulong UserId { get; set; }           // Discord User ID
    public string SessionId { get; set; }       // 추출한 conversation context ID
    public string State { get; set; }           // "active" | "inactive" (수동 관리)
    public SemanticMemoryData Extracted { get; set; }  // 이번 session 추출분
    public SemanticMemoryData Snapshot { get; set; }   // 이전 snapshot + 이번 extracted 누적
    public DateTime CreatedAt { get; set; }
}

public class SemanticMemoryData {
    public List<string> Traits { get; set; }   // 성향·선호·습관
    public List<string> Episodic { get; set; } // 경험·사건·에피소드
}
```

**설계 원칙**:
- 도큐먼트는 session별로 생성 (갱신 아님) → 히스토리 보존, rollback 가능
- `state = "inactive"` 로 수동 설정 시 해당 document는 주입에서 제외
- LLM 주입에는 가장 최신 `active` document의 `snapshot` 사용

### 처리 흐름

```
Compaction 완료 후 (호출자 오케스트레이션)
     │
     ▼
SemanticMemoryService.ExtractAndSaveAsync(context)
     │
     ├─ SemanticMemoryRefs.Any() → 이미 처리된 session → skip
     │
     ├─ LLM 호출 (responseSchema: users[]·subject/traits/episodic)
     │   └─ CacheHint 전달 (캐시 유효 시 Gemini explicit cache 활용)
     │
     └─ per-user 처리:
          ├─ Participants 매핑 (subject → userId)
          ├─ GetLatestActiveAsync → 이전 snapshot 조회
          ├─ snapshot = 이전 snapshot + 이번 extracted 누적
          ├─ CompressIfNeededAsync (trait/episodic count >= threshold 시 LLM 압축)
          │   └─ 빈 결과 반환 시 원본 보존 (데이터 손실 방지)
          ├─ CreateAsync (per-session document 신규 삽입)
          ├─ SemanticMemoryRefs.Add(userId)
          └─ SaveContextAsync
```

### 주입 흐름

```
MessageHandler.ProcessBatchAsync
     │
     └─ 첫 메시지 수신 시 (InjectedSemanticMemoryUserIds에 없는 userId)
          ├─ GetLatestSnapshotAsync(userId) per un-injected user
          ├─ BuildSemanticMemoryBlock() → <semantic_memory> 블록 생성
          ├─ LLM 메시지에만 prepend (DB 저장 원본 메시지 유지)
          └─ InjectedSemanticMemoryUserIds에 userId 기록 (session 내 1회)
```

### 주입 형식

```
<semantic_memory>
[Alice]
[Traits]
- Alice는 고양이를 좋아한다.
[Episodic]
- Alice는 도쿄를 방문했다.

[Bob]
[Traits]
- Bob은 피자를 좋아한다.
</semantic_memory>
```

### 압축 전략

- `Traits` / `Episodic` 독립 threshold (`SemanticMemory.TraitCompressionThreshold`, `EpisodicCompressionThreshold`)
- snapshot 누적 후 count >= threshold 시 LLM 압축 호출
- 빈 결과 또는 파싱 실패 시 원본 목록 보존 (데이터 손실 방지)
- Phase 2: `CompressionModel` 필드 예약 (현재 미사용)
- 상세 계약: [65_SEMANTIC_MEMORY_PLAN.md](65_SEMANTIC_MEMORY_PLAN.md)

---

## 확장 계획 (초기 버전 이후)

- [ ] 플러그인 시스템 (동적 어셈블리 로딩)
- [ ] 외부 설정 기반 커맨드 등록
- [ ] 추가 LLM Provider (OpenAI, Claude, Ollama)
- [ ] 웹 관리 UI (Bot 설정, 로그 조회)
