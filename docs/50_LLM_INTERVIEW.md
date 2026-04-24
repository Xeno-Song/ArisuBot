# LLM 서비스 연동 설계 문서

날짜: 2026-04-22 (최종 수정: 2026-04-23)  
작성자: 설계 인터뷰 기반 (Claude Code)

---

## 1. 개요

ArisuBot에 Google Gemini LLM을 연동하여 Discord 메시지에 AI 응답을 생성하는 시스템을 구현한다.  
현재 `MessageHandler`에 echo 응답만 존재하며, `ArisuBot.LLM` 프로젝트는 비어있다.  
이 문서는 인터뷰를 통해 확정된 스펙과 컴포넌트 설계를 기술한다.

---

## 2. 인터뷰 확정 스펙

### 2.1 Gemini 모델

- config 기반 런타임 교체 가능 (`LLM:Gemini:Model`)
- 기본값: `gemini-2.5-flash-lite` (Local config에서 지정)
- `LLMOptions`에 `Provider` 필드로 공급자 선택 (`"Gemini"` 고정 1차 구현)

### 2.2 시스템 프롬프트 관리

- `prompts/` 디렉토리에 `.md` 파일로 관리
- **로드 시점**: 봇 프로세스 최초 시작 시 1회 + `/new-session` 커맨드 실행 시 재로드
  - 요청마다 재로드하지 않음 (비효율)
  - 파일 수정 후 `/new-session`으로 반영 가능
- 파일 없으면 시작 시 예외 발생 (fail-fast, fallback 없음)
- 파일 구조:
  - `prompts/system.md` → `Role.System` 메시지로 **매 요청마다** 첫 번째 위치에 prepend
  - `prompts/persona.md` → `Role.User` 메시지로 **컨텍스트 최초 생성 시 1회만** DB에 저장

### 2.3 대화 컨텍스트 타입

| 메시지 출처 | Context 타입 | targetId |
|------------|--------------|----------|
| Guild 채널 메시지 | `ContextType.Channel` | `channelId` |
| DM 메시지 | `ContextType.User` | `userId` |

기존 `ContextType.Channel` / `ContextType.User` 모델 그대로 사용. 신규 타입 없음.

### 2.4 DM 응답 조건 — Whitelist

DM은 허용된 User에게만 응답한다.

- 허용 목록 = `Discord:AdminUserIds` ∪ `Discord:DmWhitelistExtraUserIds`
- 관리자는 자동으로 DM whitelist에 포함
- 목록에 없는 User의 DM은 무시 (에러/알림 없음)

### 2.5 LLM API 실패 처리

에러 종류(Rate limit / 인증 실패 / 네트워크 / 콘텐츠 필터) 구분 없이 동일하게 처리:

1. Log4Net 에러 로그 기록
2. 관리자 전원에게 Discord DM 발송 (에러 메시지 포함)
3. Discord 채널에 응답하지 않음 (봇 무응답)
4. 예외는 Handler 레벨에서 catch — LLM 레이어에서 swallow 금지

### 2.6 스트리밍 및 Typing Indicator

- Gemini `GenerateContentStreamAsync` 호출 (스트리밍 API 사용)
- 모든 청크 누적 후 완성 시 Discord에 전송
- `TriggerTypingAsync()` — LLM 호출 직전 1회 호출
- 10초 초과 소멸해도 무시 (루프 재호출 없음)

### 2.7 메시지 저장 순서

```
1. AppendMessageAsync(유저 메시지)
2. ILLMProvider.GenerateAsync(messages)  → IReadOnlyList<LLMResponse>
3. AppendMessageAsync(봇 응답 전체 합산 — Role.Assistant 단일 메시지)
4. IAIMessageLogger.LogAsync(...)
5. Discord 채널 메시지 전송 (복수 가능)
```

LLM 실패 시 유저 메시지는 이미 저장된 상태 (스펙 확정, 롤백 없음).

### 2.8 LLM 파라미터

`LLMOptions`에 `MaxTokens`, `Temperature` 포함. config 기반으로 Gemini `GenerationConfig`에 전달.  
현재는 기본값 사용, 차후 사용자 설정 확장 예정.

### 2.9 LLM 복수 응답 처리

`ILLMProvider.GenerateAsync`는 `Task<IReadOnlyList<LLMResponse>>`를 반환한다.

**복수 응답이 발생하는 경우:**
- 현재: Discord 2000자 제한 초과 → 청크 분할 전송
- 차후: Tool use 결과로 복수 응답 턴 발생 가능

**Discord 전송 규칙:**
- 각 `LLMResponse.Content`를 2000자 단위로 분할
- 분할된 각 조각을 순서대로 `SendMessageAsync`
- **멘션 응답**: 전체 메시지 세트의 모든 조각에 reply reference 포함
- **일반 메시지 응답**: 전체 메시지 세트를 reference 없는 일반 메시지로 전송

**DB 저장 규칙:**
- `IReadOnlyList<LLMResponse>` 전체 Content를 줄바꿈(`\n`)으로 이어붙여 단일 `Role.Assistant` 메시지로 저장
- 대화 컨텍스트의 연속성 유지 목적

### 2.9.1 토큰 사용량 추적 (2026-04-23 추가)

요청별 토큰 사용량을 `conversation_contexts` 도큐먼트의 `tokenUsage` 배열에 저장한다.

| 필드 | 출처 | 설명 |
|------|------|------|
| `tokensIn` | `UsageMetadata.PromptTokenCount` | 입력 토큰 수 (프롬프트 + 히스토리) |
| `tokensOut` | `UsageMetadata.CandidatesTokenCount` | 출력 토큰 수 (생성된 응답) |
| `tokensCachedIn` | `UsageMetadata.CachedContentTokenCount` | 입력 중 implicit cache 히트 토큰 수 |

**Implicit Caching:** Gemini 2.5 모델은 동일 prefix가 반복될 때 서버 측에서 자동 캐싱.  
별도 API 호출 없이 `CachedContentTokenCount` non-zero 시 캐시 히트 확인 가능.

**저장 구조:** `conversation_contexts.tokenUsage[]` — LLM 요청마다 append  
세션 단위로 토큰 이력이 누적되며, 신규 세션 생성 시 초기화됨.

**데이터 흐름:**
```
GeminiProvider — 스트리밍 마지막 청크에서 UsageMetadata 수집
  → LLMResponse.TokensIn / TokensOut / TokensCachedIn
  → MessageHandler — responses 합산 → TokenUsage 생성
  → ConversationService.AppendTokenUsageAsync(context, usage)
  → conversation_contexts.tokenUsage[] 에 append 저장
```

### 2.10 `/new-session` 슬래시 커맨드

- **실행 권한**: 누구나 (초기 구현, 차후 권한 제어 예정)
- **동작**:
  1. 현재 채널(Guild) 또는 DM(User)에 새 빈 `conversation_contexts` 도큐먼트 생성 (`StartNewSessionAsync`)
  2. `IPromptLoader.Reload()` 호출 → `system.md`, `persona.md` 재로드
  3. Discord에 완료 메시지 응답
- **범위**: 실행된 채널/DM의 컨텍스트만 대상. 다른 채널 영향 없음.
- **프롬프트 재로드**: 전역 적용 (모든 채널에서 새 프롬프트 사용)

### 2.11 세션 관리 — 멀티 도큐먼트 방식

`/new-session` 실행 시 기존 도큐먼트를 **삭제하지 않고** 히스토리로 보존한다.

**도큐먼트 구조:**
- `conversation_contexts` 컬렉션에 동일 `targetId`와 `type`으로 복수 도큐먼트가 존재할 수 있음
- 각 도큐먼트는 `createdAt` 필드로 생성 시각을 기록
- `GetContextAsync` 조회 시 `createdAt` 내림차순 정렬 → **가장 최근 세션** 반환

**변경 이력 (2026-04-23):**
- `ConversationContext` 도메인 모델에 `CreatedAt` 필드 추가
- `ConversationDocument`에 `createdAt` BSON 필드 추가
- `IConversationRepository`에서 `ClearContextAsync` 제거 → `CreateNewSessionAsync` 추가
- `ConversationService`에서 `ClearContextAsync` 제거 → `StartNewSessionAsync` 추가
- `ConversationRepository.GetOrCreateAsync`: `SortByDescending(d => d.CreatedAt)` 적용
- `NewSessionCommand`: `ClearContextAsync` → `StartNewSessionAsync` 호출로 변경

---

## 3. Config 스키마

### 3.1 appsettings.json (non-secret, 커밋 가능)

```json
{
  "Discord": {
    "DevGuildId": "",
    "AdminUserIds": [],
    "DmWhitelistExtraUserIds": [],
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
  }
}
```

### 3.2 appsettings.Local.json (secret, 커밋 금지)

```json
{
  "Discord": {
    "Token": "DISCORD_BOT_TOKEN"
  },
  "LLM": {
    "Gemini": {
      "ApiKey": "GEMINI_API_KEY",
      "Model": "gemini-2.5-flash-lite"
    }
  },
  "MongoDB": {
    "ConnectionString": "mongodb://..."
  }
}
```

---

## 4. 컴포넌트 설계

### 4.1 신규 인터페이스 — Core 레이어

#### `IPromptLoader`

```csharp
namespace ArisuBot.Core.Interfaces;

/// <summary>프롬프트 파일을 로드하고 캐싱한다. /new-session 시 Reload()로 갱신.</summary>
public interface IPromptLoader
{
    /// <summary>system.md 내용. Role.System으로 매 요청 prepend.</summary>
    string SystemPrompt { get; }

    /// <summary>persona.md 내용. 컨텍스트 최초 생성 시 Role.User로 1회 주입.</summary>
    string PersonaPrompt { get; }

    /// <summary>파일을 다시 읽어 캐시를 갱신한다. /new-session 호출 시 사용.</summary>
    void Reload();
}
```

#### `IAdminNotifier`

```csharp
namespace ArisuBot.Core.Interfaces;

/// <summary>관리자 Discord 계정에 알림 메시지를 전송한다.</summary>
public interface IAdminNotifier
{
    Task NotifyAsync(string message, CancellationToken ct = default);
}
```

#### `ILLMProvider` — 반환 타입 변경

```csharp
namespace ArisuBot.Core.Interfaces;

/// <summary>LLM 공급자 추상화. 구현체 교체 시 호출부 변경 없음.</summary>
public interface ILLMProvider
{
    string ProviderName { get; }

    /// <summary>
    /// 메시지 목록으로 LLM 응답을 생성한다.
    /// 단일 응답이면 요소 1개, 복수 응답(tool use 등)이면 요소 N개.
    /// </summary>
    Task<IReadOnlyList<LLMResponse>> GenerateAsync(
        IEnumerable<ChatMessage> messages, CancellationToken ct = default);
}
```

---

### 4.2 신규 Options 클래스

#### `LLMOptions` (ArisuBot.LLM)

```csharp
namespace ArisuBot.LLM.Options;

/// <summary>LLM 공통 설정. Section: "LLM".</summary>
public class LLMOptions
{
    public const string SectionName = "LLM";

    /// <summary>사용할 LLM 공급자 이름. 현재: "Gemini".</summary>
    public string Provider { get; set; } = "Gemini";

    /// <summary>최대 출력 토큰 수.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>생성 온도. 0.0–1.0.</summary>
    public float Temperature { get; set; } = 0.7f;
}
```

#### `GeminiOptions` (ArisuBot.LLM)

```csharp
namespace ArisuBot.LLM.Options;

/// <summary>Gemini 공급자 전용 설정. Section: "LLM:Gemini".</summary>
public class GeminiOptions
{
    public const string SectionName = "LLM:Gemini";

    /// <summary>Gemini API 키. 반드시 appsettings.Local.json에서 주입.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>사용할 Gemini 모델 ID.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";
}
```

#### `DiscordOptions` 수정 (ArisuBot.Discord)

```csharp
// 기존 필드 유지, 아래 두 필드 추가
/// <summary>에러 발생 시 DM 알림을 받을 관리자 User ID 목록. DM whitelist에도 자동 포함.</summary>
public ulong[] AdminUserIds { get; set; } = [];

/// <summary>관리자 외 DM 응답을 허용할 추가 User ID 목록.</summary>
public ulong[] DmWhitelistExtraUserIds { get; set; } = [];
```

---

### 4.3 GeminiProvider (ArisuBot.LLM)

**책임**: `ILLMProvider` 구현. `ChatMessage[]` → Gemini `Content[]` 변환 후 스트리밍 호출, 응답 누적 후 단일 항목 리스트 반환. 현재 tool use 미지원 — 1개 항목 리스트만 반환.

**핵심 동작:**

1. `ChatMessage` Role 매핑:

   | Core Role | Gemini 처리 방식 |
   |-----------|-----------------|
   | `Role.System` | `GenerateContentRequest.SystemInstruction` 별도 파라미터 |
   | `Role.User` | `Content { Role = "user" }` |
   | `Role.Assistant` | `Content { Role = "model" }` |

2. `GenerateContentStreamAsync` 호출 → 청크 `StringBuilder`로 누적 → 완성 시 `LLMResponse` 1개를 리스트로 반환

3. `GenerationConfig`에 `MaxOutputTokens`, `Temperature` 적용

4. 예외는 caller(`MessageHandler`)로 전파 — 내부 catch 금지

**클래스 구조:**

```csharp
namespace ArisuBot.LLM.Gemini;

/// <summary>Google Gemini API를 사용하는 ILLMProvider 구현체.</summary>
public class GeminiProvider : ILLMProvider
{
    public string ProviderName => "Gemini";

    // 생성자: IOptions<GeminiOptions>, IOptions<LLMOptions> 주입
    // GenerateAsync:
    //   - SystemInstruction = messages where Role.System (첫 번째)
    //   - Contents = messages where Role != System
    //   - GenerateContentStreamAsync 호출 → 청크 누적
    //   - return new List<LLMResponse> { new(accumulatedText, ProviderName) }
}
```

---

### 4.4 FilePromptLoader (ArisuBot.Infrastructure)

**책임**: `prompts/` 디렉토리에서 파일 읽기. 시작 시 로드, `Reload()` 호출 시 재로드. 파일 없으면 `FileNotFoundException` (fail-fast).

```csharp
namespace ArisuBot.Infrastructure.Prompts;

/// <summary>파일 시스템에서 프롬프트를 로드한다. Reload()로 갱신 가능.</summary>
public class FilePromptLoader : IPromptLoader
{
    private readonly string _promptsDirectory;
    public string SystemPrompt { get; private set; }
    public string PersonaPrompt { get; private set; }

    public FilePromptLoader(string promptsDirectory)
    {
        _promptsDirectory = promptsDirectory;
        // 생성자에서 즉시 로드 (봇 시작 시 fail-fast)
        Reload();
    }

    public void Reload()
    {
        // File.ReadAllText(system.md) → SystemPrompt
        // File.ReadAllText(persona.md) → PersonaPrompt
        // 파일 없으면 FileNotFoundException 전파
    }
}
```

---

### 4.5 AdminNotifier (ArisuBot.Discord)

**책임**: `DiscordSocketClient`로 `AdminUserIds` 전원에게 DM 전송. 내부 실패 시 로그만 기록 (2차 알림 없음, 무한루프 방지).

```csharp
namespace ArisuBot.Discord.Services;

/// <summary>관리자 계정에 Discord DM으로 알림을 전송한다.</summary>
public class AdminNotifier : IAdminNotifier
{
    // 생성자: DiscordSocketClient, IOptions<DiscordOptions>, ILogger 주입
    // NotifyAsync:
    //   AdminUserIds 순회 → GetOrCreateDMChannelAsync → SendMessageAsync
    //   DM 전송 실패 시: LogError만 기록, 예외 swallow (무한루프 방지)
}
```

---

### 4.6 MessageHandler 재설계 (ArisuBot.Discord)

**책임**: 메시지 수신 → 응답 여부 판단 → LLM 호출 오케스트레이션 → 복수 Discord 메시지 전송.

**추가 DI 주입:**

```csharp
public MessageHandler(
    DiscordSocketClient client,
    IOptions<DiscordOptions> options,
    ConversationService conversationService,
    ILLMProvider llmProvider,
    IAIMessageLogger messageLogger,
    IAdminNotifier adminNotifier,
    IPromptLoader promptLoader,
    ILogger<MessageHandler> logger)
```

**HandleAsync 처리 흐름:**

```
1. Bot 메시지 → return
2. SocketUserMessage 아니면 → return
3. DM 채널(SocketDMChannel):
   - whitelist = AdminUserIds ∪ DmWhitelistExtraUserIds
   - userId not in whitelist → return
   - contextType = User, targetId = userId, guildId = 0
4. Guild 채널(SocketGuildChannel):
   - ShouldRespond() false → return
   - contextType = Channel, targetId = channelId, guildId = guild.Id
5. await TriggerTypingAsync()
6. try:
   a. context = GetContextAsync(targetId, contextType)
   b. context.Messages.Count == 0:
      → AppendMessageAsync(context, Role.User, promptLoader.PersonaPrompt)
   c. AppendMessageAsync(context, Role.User, userMessage.Content)
   d. messages = BuildMessageList(context, userMessage.Content, promptLoader.SystemPrompt)
   e. responses = await llmProvider.GenerateAsync(messages)
   f. combinedContent = string.Join("\n", responses.Select(r => r.Content))
   g. AppendMessageAsync(context, Role.Assistant, combinedContent)
   h. AIMessageLogger.LogAsync(guildId, channelId, userId, userMsg, combinedContent, providerName)
   i. Discord 전송:
      - responses 순회 → 각 Content를 2000자 청크로 분할
      - isMention == true: 모든 청크에 reply reference 포함
      - isMention == false: 모든 청크를 reference 없는 일반 메시지로 전송
   catch Exception ex:
   - logger.LogError(ex, ...)
   - await adminNotifier.NotifyAsync(에러 요약)
   - return (Discord 무응답)
```

**Discord 2000자 분할 헬퍼 (내부 메서드):**

```csharp
// 문자열을 maxLength 단위로 분할
private static IEnumerable<string> SplitIntoChunks(string text, int maxLength = 2000)
```

---

### 4.7 NewSessionCommand (ArisuBot.Discord)

**책임**: `/new-session` 슬래시 커맨드. 현재 채널/DM 컨텍스트 초기화 + 프롬프트 재로드.

```csharp
namespace ArisuBot.Discord.Commands;

/// <summary>/new-session: 대화 컨텍스트 초기화 및 프롬프트 재로드.</summary>
public class NewSessionCommand : InteractionModuleBase<SocketInteractionContext>
{
    // 생성자: ConversationService, IPromptLoader 주입

    [SlashCommand("new-session", "대화를 초기화하고 프롬프트를 재로드합니다.")]
    public async Task ExecuteAsync()
    {
        // 1. DM or Guild 판단 → contextType, targetId 결정 (MessageHandler와 동일 로직)
        // 2. ClearContextAsync(context)
        // 3. promptLoader.Reload()
        // 4. RespondAsync("세션이 초기화되었습니다.", ephemeral: true)
    }
}
```

**주의**: `promptLoader.Reload()`는 전역 적용 — 모든 채널에서 새 프롬프트 사용.

---

### 4.8 LLMServiceExtensions (ArisuBot.LLM)

```csharp
namespace ArisuBot.LLM.Extensions;

/// <summary>LLM 관련 DI 등록 확장 메서드.</summary>
public static class LLMServiceExtensions
{
    public static IServiceCollection AddLLMProvider(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LLMOptions>(configuration.GetSection(LLMOptions.SectionName));
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        // Provider 선택 (현재: Gemini 고정)
        services.AddSingleton<ILLMProvider, GeminiProvider>();
        return services;
    }
}
```

---

## 5. 의존성 그래프

```
ArisuBot.Host
  └── Program.cs
        ├── ArisuBot.Discord
        │     ├── MessageHandler
        │     │     ├── ILLMProvider          (← ArisuBot.LLM)
        │     │     ├── ConversationService   (← ArisuBot.Core)
        │     │     ├── IAIMessageLogger      (← ArisuBot.Infrastructure)
        │     │     ├── IAdminNotifier        (← ArisuBot.Discord.Services)
        │     │     └── IPromptLoader         (← ArisuBot.Infrastructure)
        │     ├── AdminNotifier
        │     │     └── DiscordSocketClient
        │     └── NewSessionCommand
        │           ├── ConversationService   (← ArisuBot.Core)
        │           └── IPromptLoader         (← ArisuBot.Infrastructure)
        ├── ArisuBot.LLM
        │     └── GeminiProvider
        │           ├── Google.GenAI
        │           ├── LLMOptions
        │           └── GeminiOptions
        └── ArisuBot.Infrastructure
              ├── FilePromptLoader
              │     └── prompts/*.md
              ├── ConversationRepository
              └── AIMessageLogger
```

---

## 6. 메시지 처리 시퀀스

```
Discord     MessageHandler    ConversationService    GeminiProvider       Discord
   │               │                  │                    │                 │
   │──메시지 수신──▶│                  │                    │                 │
   │               │──whitelist/ShouldRespond 확인         │                 │
   │               │──TriggerTypingAsync()─────────────────────────────────▶│
   │               │──GetContextAsync()──▶│                │                 │
   │               │◀──context──────────│                  │                 │
   │               │  [최초] AppendMessageAsync(persona)   │                 │
   │               │──AppendMessageAsync(user msg)──▶│     │                 │
   │               │──BuildMessageList()─▶│           │     │                 │
   │               │◀──messages[]────────│            │     │                 │
   │               │──GenerateAsync(messages)───────────▶│  │                 │
   │               │  [스트리밍 누적]                  │  │                 │
   │               │◀──IReadOnlyList<LLMResponse>─────────│  │                 │
   │               │──AppendMessageAsync(combined)──▶│    │                 │
   │               │──AIMessageLogger.LogAsync()     │    │                 │
   │               │──[2000자 분할 후 순차 전송]──────────────────────────▶│
   │               │                    │            │                      │
```

### /new-session 시퀀스

```
Discord     NewSessionCommand    ConversationService    FilePromptLoader
   │               │                  │                    │
   │──/new-session▶│                  │                    │
   │               │──ClearContextAsync()──▶│              │
   │               │──Reload()─────────────────────────────▶│
   │               │  [system.md, persona.md 재로드]        │
   │               │──RespondAsync("세션 초기화 완료")       │
   │◀──응답(ephemeral)│                  │                    │
```

---

## 7. 에러 처리 상세

```
try { LLM 호출 흐름 }
catch (Exception ex)
{
    // 1. 로그 기록 (Log4Net)
    logger.LogError(ex, "LLM 호출 실패 — channelId={ChannelId} userId={UserId}", ...)

    // 2. 관리자 DM 전송 (AdminNotifier 내부 실패는 LogError만, 재알림 없음)
    await adminNotifier.NotifyAsync(
        $"[ArisuBot 오류] {ex.GetType().Name}: {ex.Message}\nChannel: {channelId}\nUser: {userId}");

    // 3. Discord 무응답 (return)
}
```

---

## 8. 프롬프트 파일 규칙

- 경로: `{ContentRoot}/prompts/system.md`, `{ContentRoot}/prompts/persona.md`
- `ContentRoot` = `AppContext.BaseDirectory` (Host bin 디렉토리)
- 빌드 복사: `ArisuBot.Host.csproj`에 `CopyToOutputDirectory: Always` 설정 필요
- 형식: 자유 텍스트 Markdown (LLM에 그대로 전달)
- 인코딩: UTF-8

---

## 9. 신규/수정 파일 목록

### 신규

| 위치 | 파일 | 설명 |
|------|------|------|
| `ArisuBot.Core/Interfaces/` | `IPromptLoader.cs` | 프롬프트 로딩 추상화 (Reload() 포함) |
| `ArisuBot.Core/Interfaces/` | `IAdminNotifier.cs` | 관리자 알림 추상화 |
| `ArisuBot.LLM/Gemini/` | `GeminiProvider.cs` | ILLMProvider 구현체 |
| `ArisuBot.LLM/Options/` | `LLMOptions.cs` | 공통 LLM 설정 |
| `ArisuBot.LLM/Options/` | `GeminiOptions.cs` | Gemini 전용 설정 |
| `ArisuBot.LLM/Extensions/` | `LLMServiceExtensions.cs` | DI 등록 확장 메서드 |
| `ArisuBot.Discord/Services/` | `AdminNotifier.cs` | IAdminNotifier 구현체 |
| `ArisuBot.Discord/Commands/` | `NewSessionCommand.cs` | /new-session 슬래시 커맨드 |
| `ArisuBot.Infrastructure/Prompts/` | `FilePromptLoader.cs` | IPromptLoader 구현체 |
| `ArisuBot.Host/prompts/` | `system.md` | 시스템 프롬프트 파일 |
| `ArisuBot.Host/prompts/` | `persona.md` | 페르소나 프롬프트 파일 |

### 수정

| 파일 | 변경 내용 |
|------|----------|
| `ArisuBot.Core/Interfaces/ILLMProvider.cs` | 반환 타입 `Task<LLMResponse>` → `Task<IReadOnlyList<LLMResponse>>` |
| `ArisuBot.Discord/Options/DiscordOptions.cs` | `AdminUserIds[]`, `DmWhitelistExtraUserIds[]` 추가 |
| `ArisuBot.Discord/Handlers/MessageHandler.cs` | DM/Guild 분기, LLM 연동, 복수 응답 처리, 에러 처리 전면 재작성 |
| `ArisuBot.Host/Program.cs` | LLM, FilePromptLoader, AdminNotifier DI 등록 추가 |
| `ArisuBot.Host/ArisuBot.Host.csproj` | prompts/ 디렉토리 CopyToOutputDirectory 설정 추가 |
| `ArisuBot.Host/appsettings.json` | LLM, AdminUserIds, DmWhitelistExtraUserIds 섹션 추가 |
| `docs/00_ARCHITECTURE.md` | LLM 연동 흐름 및 컴포넌트 업데이트 |

---

## 10. 테스트 전략

### Unit Tests (목표 ≥ 90%)

| 테스트 클래스 | 검증 대상 |
|--------------|----------|
| `GeminiProviderTests` | Role 매핑, System instruction 분리, 스트리밍 누적, 빈 응답, 예외 전파 |
| `MessageHandlerTests` | DM whitelist 허용/거부, Guild ShouldRespond, 최초 persona 주입, 복수 응답 2000자 분할, 에러 시 AdminNotifier 호출, 메시지 저장 순서 |
| `FilePromptLoaderTests` | 정상 로드, Reload() 동작, `system.md` 없을 때 예외, `persona.md` 없을 때 예외 |
| `AdminNotifierTests` | 단일/복수 관리자 DM 전송, DM 실패 시 예외 swallow |
| `NewSessionCommandTests` | 컨텍스트 초기화 호출, Reload() 호출, ephemeral 응답 |

### Integration Tests (목표 ≥ 60%)

| 테스트 | 방법 |
|--------|------|
| GeminiProvider 응답 파싱 | `HttpClient` stub (실 API 미호출) |
| MessageHandler → ConversationService → Repository | EphemeralMongo 사용 |
| /new-session → ClearContext → Reload | EphemeralMongo + 임시 파일 |

---

## 11. 미결 사항

| 항목 | 내용 |
|------|------|
| `/new-session` 권한 | 초기: 누구나. 차후 Discord Role 기반 권한 제어 예정. |
| Prompt 변경 알림 | 현재: Reload() 전역 적용. 차후: 채널별 재로드 알림 고려 가능. |

---

## 12. LLM Tool Use 설계 (2026-04-23 추가)

### 12.1 개요

LLM이 대화 중 Discord 작업(유저 타임아웃, 채널 멤버 조회 등)을 직접 실행할 수 있도록 함수 호출(Function Calling) 기능을 구현한다.

### 12.2 확정 스펙

| 항목 | 내용 |
|------|------|
| 트리거 | 모든 유저가 대화로 트리거 가능. 유저는 툴 존재 모름, LLM이 적절히 판단하여 사용 |
| 툴 범위 | Guild 채널 메시지에서만 활성화. DM에서는 비활성화(서버 컨텍스트 없음) |
| Timeout 범위 | `Discord:Tools:TimeoutMinSeconds` ~ `Discord:Tools:TimeoutMaxSeconds` (config 기반) |
| List channel users | 채널 ViewChannel 권한 보유 멤버 목록. `GuildMembers` Privileged Intent 필요 |
| 루프 최대 반복 | `LLM:MaxToolIterations` (기본값 5) 초과 시 `InvalidOperationException` |

### 12.3 구현된 툴

#### `discord_timeout_user`
- **파라미터**: `userId` (string, 필수), `durationSeconds` (integer, 필수), `reason` (string, 선택)
- **동작**: 지정 유저에게 TimeSpan 타임아웃 적용 (`IGuildUser.SetTimeOutAsync`)
- **범위 검증**: min/max 초과 시 오류 문자열 반환 (예외 아님 — LLM에 피드백)
- **봇 권한 필요**: Moderate Members

#### `discord_list_channel_users`
- **파라미터**: `channelId` (string, 선택. 생략 시 현재 채널)
- **동작**: 채널 ViewChannel 권한 보유 비봇 멤버 목록 반환 (이름 + ID)
- **봇 권한 필요**: 없음 (권한 계산은 클라이언트 로컬)

### 12.4 아키텍처

```
MessageHandler
  ├── guildId != 0 → tools=[TimeoutUserTool, ListChannelUsersTool], toolContext={guildId, channelId}
  └── DM           → tools=[], toolContext=null → 툴 비활성화

ILLMProvider.GenerateAsync(messages, tools, toolContext, ct)
  └── GeminiProvider
        ├── tools+toolContext 있음 → config.Tools에 FunctionDeclaration 추가
        └── 함수 호출 루프 (최대 MaxToolIterations 회)
              ├── StreamAsync → 텍스트/FunctionCalls 누적
              ├── FunctionCalls 없음 → 최종 LLMResponse 반환
              └── FunctionCalls 있음
                    ├── contents에 model FunctionCall 추가
                    ├── ILLMTool.ExecuteAsync 실행
                    ├── contents에 user FunctionResponse 추가
                    └── 재호출
```

### 12.5 토큰 집계

툴 호출 루프 전체 반복의 토큰 합산 후 단일 `LLMResponse`에 반환:
- `TokensIn` = 모든 스트리밍 호출의 `PromptTokenCount` 합산
- `TokensOut` = 모든 스트리밍 호출의 `CandidatesTokenCount` 합산

### 12.6 인프라 요구사항

| 항목 | 설명 |
|------|------|
| Gateway Intent | `GatewayIntents.GuildMembers` 추가 (Privileged — Discord Developer Portal 수동 활성화 필요) |
| 봇 권한 | Moderate Members (타임아웃 실행) |
| appsettings.json | `Discord:Tools:TimeoutMinSeconds`, `Discord:Tools:TimeoutMaxSeconds`, `LLM:MaxToolIterations` |

### 12.7 버그 수정 기록

#### `ParametersJsonSchema` 타입 오류 (2026-04-23)

**증상**: `Google.GenAI.ClientError: schema at top-level must be a boolean or an object`

**원인**: `FunctionDeclaration.ParametersJsonSchema`가 `object?` 타입임에도 `string`을 전달.  
`System.Text.Json`은 `string` 값을 JSON string literal `"{ ... }"`로 직렬화 → Gemini API가 top-level에서 object가 아닌 string 수신 → 거부.  
`.Trim()` 만으로는 해결 불가. 타입 자체가 문제.

**수정**: `JsonSerializer.Deserialize<JsonElement>(schema.Trim())`로 파싱 후 전달.  
`JsonElement`는 raw JSON object로 직렬화되어 API가 정상 수신.

```csharp
// Before (잘못됨)
ParametersJsonSchema = t.Definition.ParametersJsonSchema.Trim()

// After (올바름)
ParametersJsonSchema = JsonSerializer.Deserialize<JsonElement>(
    t.Definition.ParametersJsonSchema.Trim())
```

**위치**: `GeminiProvider.BuildToolDeclarations`

---

### 12.8 신규/수정 파일

| 위치 | 파일 | 설명 |
|------|------|------|
| `ArisuBot.Core/Models/` | `LLMToolDefinition.cs` | 툴 정의 record |
| `ArisuBot.Core/Models/` | `LLMToolExecutionContext.cs` | 툴 실행 컨텍스트 record |
| `ArisuBot.Core/Interfaces/` | `ILLMTool.cs` | 툴 실행 인터페이스 |
| `ArisuBot.Discord/Options/` | `DiscordToolOptions.cs` | Timeout min/max 설정 |
| `ArisuBot.Discord/Tools/` | `TimeoutUserTool.cs` | 유저 타임아웃 툴 |
| `ArisuBot.Discord/Tools/` | `ListChannelUsersTool.cs` | 채널 멤버 목록 툴 |
| `ArisuBot.LLM/Gemini/` | `GeminiProvider.cs` | 함수 호출 루프 구현 |
| `ArisuBot.Core/Interfaces/` | `ILLMProvider.cs` | tools/toolContext 파라미터 추가 |
| `ArisuBot.LLM/Options/` | `LLMOptions.cs` | `MaxToolIterations` 추가 |
| `ArisuBot.Discord/Handlers/` | `MessageHandler.cs` | `IEnumerable<ILLMTool>` 주입, toolContext 생성 |
| `ArisuBot.Host/` | `Program.cs` | 툴 DI 등록, GuildMembers intent |
| `ArisuBot.Host/` | `appsettings.json` | Discord:Tools, LLM:MaxToolIterations |
