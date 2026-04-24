# LLM Tool 구현 명세

날짜: 2026-04-23  
작성자: Claude Code

---

## 1. 개요

LLM이 대화 중 Discord 작업을 직접 실행하기 위한 함수 호출(Function Calling) 구조를 정의한다.  
Tool은 LLM의 요청에 의해 실행되며, 각 호출은 내부 GUID로 추적된다. LLM은 GUID를 생성하거나 인식할 수 없다.

---

## 2. Discord Tool 전역 규칙

Discord 관련 LLM Tool 전체에 공통 적용되는 설계 원칙. 신규 Tool 추가 시 반드시 준수.

---

### 2.1 채널·서버 컨텍스트는 시스템이 제공한다

- LLM은 `channelId`, `guildId` 등 Discord 내부 숫자 ID를 인수(argument)로 전달하지 않는다.
- 채널/서버 정보는 메시지 수신 시 `LLMToolExecutionContext`에 의해 시스템이 주입한다.
- 각 Tool 구현체는 `context.ChannelId`, `context.GuildId`를 통해 접근한다.
- 채널 지정이 필요한 Tool은 현재 채널(context.ChannelId)을 항상 기본값으로 사용한다.

**근거**: 대화 채널과 서버 정보는 Discord 이벤트에서 이미 확정되며, LLM이 숫자 ID를 직접 생성하거나 기억하게 하면 오인식 위험이 높다.

---

### 2.2 유저 식별은 displayName 기준이다

- LLM은 `userId` (Discord 숫자 snowflake ID)를 인수로 받거나 반환하지 않는다.
- 모든 Tool의 유저 식별 파라미터는 `userName` (displayName) 사용.
- Tool 구현체 내부에서 `guild.Users.FirstOrDefault(u => u.DisplayName == userName)` 방식으로 resolve한다.
- ToolResult 응답에도 `userId`를 포함하지 않는다. 사람이 읽을 수 있는 `displayName`만 포함한다.
- 멤버 목록(list) 응답 역시 displayName만 반환한다 (`["Alice", "Bob"]`). ID 제외.

**근거**: 대화에서 유저는 항상 `[displayName]: 메시지` 형태로 언급된다. LLM이 숫자 ID를 직접 입력·기억하게 하면 혼동 및 오입력 가능성이 크다.

---

## 3. Tool Request 구조

LLM이 Tool 호출을 요청할 때 전달되는 데이터.

```json
{
  "toolName": "discord_timeout_user",
  "arguments": {
    "userName": "Xeno",
    "durationSeconds": 300,
    "reason": "욕설"
  }
}
```

### 규칙

- `toolName`: Tool 식별자. `ILLMTool.Name`과 일치해야 함
- `arguments`: Tool 정의(`ParametersJsonSchema`)에 따른 구조화된 JSON
  - `required` 필드: 반드시 포함
  - `optional` 필드: 생략 가능, top-level 위치
- `arguments` 내 필드 타입은 JSON Schema 기준을 따름 (`string`, `integer`, `boolean` 등)

### 내부 관리 데이터 (LLM 비노출)

| 필드 | 타입 | 설명 |
|------|------|------|
| `callId` | `Guid` | 호출별 고유 식별자. 로그 연관 및 대화 이력 추적 용도 |

`callId`는 `GeminiProvider`가 FunctionCall 수신 시 `Guid.NewGuid()`로 생성하며, LLM에 전달되는 FunctionResponse에 포함되지 않는다.

---

## 4. Tool Response 구조

Tool 실행 결과. 반드시 JSON 형태이며, `success` 필드를 기본으로 포함한다.

### 성공 응답

```json
{
  "success": true,
  // tool별 추가 필드
}
```

### 실패 응답

```json
{
  "success": false,
  "error": "오류 메시지"
}
```

### Tool별 응답 예시

#### `discord_timeout_user`

```json
// 성공
{
  "success": true,
  "displayName": "Xeno",
  "durationSeconds": 300,
  "reason": "욕설"
}

// 실패
{
  "success": false,
  "error": "userName 파라미터가 필요합니다."
}
```

#### `discord_list_channel_users`

```json
// 성공
{
  "success": true,
  "memberCount": 3,
  "members": [
    "Alice",
    "Bob",
    "Carol"
  ]
}

// 실패
{
  "success": false,
  "error": "요청에 실패했습니다."
}
```

---

## 5. 인터페이스 설계 (목표 상태)

### `ILLMTool`

```csharp
public interface ILLMTool
{
    string Name { get; }
    LLMToolDefinition Definition { get; }

    /// <summary>
    /// Tool 실행. 반환값은 JSON 직렬화된 ToolResult.
    /// </summary>
    Task<ToolResult> ExecuteAsync(
        IReadOnlyDictionary<string, object> arguments,
        LLMToolExecutionContext context,
        CancellationToken ct = default);
}
```

### `ToolResult` (기반 레코드)

```csharp
/// <summary>
/// 모든 Tool 응답의 기반 레코드.
/// JSON 직렬화 후 Gemini FunctionResponse.Response에 포함된다.
/// </summary>
public record ToolResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>실패 결과 생성 헬퍼.</summary>
    public static ToolResult Fail(string error) => new() { Success = false, Error = error };
}
```

### Tool별 Result 레코드 예시

```csharp
public record TimeoutUserResult : ToolResult
{
    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public record ListChannelUsersResult : ToolResult
{
    [JsonPropertyName("channelId")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("memberCount")]
    public int MemberCount { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = [];
}
```

---

## 6. 호출 흐름

```
GeminiProvider.GenerateAsync()
  │
  ├─ Gemini API 스트리밍 → FunctionCalls 수신
  │
  └─ foreach FunctionCall:
       ├─ callId = Guid.NewGuid()          ← 내부 추적용, LLM 비노출
       ├─ _logger.LogInformation("툴 호출 감지 — tool={Tool}", fc.Name)
       │
       ├─ [try] tool.ExecuteAsync(args, context)
       │     ├─ 정상 반환    → ToolResult { success: true/false, ... }
       │     ├─ OperationCanceledException → re-throw (iteration 중단)
       │     └─ 기타 Exception
       │           ├─ _logger.LogError(ex, "툴 실행 예외 — ...")
       │           └─ ToolResult.Fail(ex.Message)   ← LLM iteration 유지
       │
       ├─ _logger.LogInformation("툴 실행 완료 — success={Success}", result.Success)
       │
       └─ FunctionResponse.Response = { "result": JsonSerialize(toolResult) }
            ← ToolResult JSON이 LLM에 전달됨 (callId 제외)
```

### 6.1 예외 처리 계약

Tool 구현체(`ILLMTool.ExecuteAsync`)와 `GeminiProvider` 간의 예외 처리 책임 분리.

| 예외 유형 | 처리 주체 | 처리 방식 |
|-----------|-----------|-----------|
| 예측 가능한 실패 (권한 없음, 대상 없음 등) | **Tool 구현체** | `ToolResult.Fail(message)` 반환 |
| 예측 불가능한 런타임 예외 (`HttpException` 등) | **GeminiProvider** | `LogError` + `ToolResult.Fail(ex.Message)` |
| 취소 요청 (`OperationCanceledException`) | **GeminiProvider** | re-throw — iteration 즉시 중단 |

**근거**: Tool 구현체가 예외를 throw하면 LLM iteration 전체가 중단되어 사용자에게 에러만 반환된다. GeminiProvider가 예외를 잡아 `ToolResult.Fail`로 변환하면 LLM이 실패 사유를 인지하고 대화를 이어갈 수 있다.

**Tool 구현체 권고사항**: 예측 가능한 실패(파라미터 오류, Discord 권한 부족, 대상 미존재 등)는 try-catch로 잡아 `ToolResult.Fail`로 반환할 것. GeminiProvider의 catch는 최후 안전망(last-resort)으로만 동작함.

---

## 7. 현재 구현 vs 목표 상태

| 항목 | 현재 | 목표 | 완료 |
|------|------|------|------|
| `ExecuteAsync` 반환 타입 | `Task<string>` (plain text) | `Task<ToolResult>` (구조화) | ✅ 2026-04-23 |
| Tool 응답 형식 | 평문 한국어 문자열 | JSON (`success` 포함) | ✅ 2026-04-23 |
| 오류 표현 | `"오류: ..."` 접두사 평문 | `{ "success": false, "error": "..." }` | ✅ 2026-04-23 |
| 호출 추적 | 없음 | `callId` (Guid) | ✅ 2026-04-23 |
| 대화 이력 저장 | 없음 | `conversation_contexts`에 ToolCall/ToolResponse 저장 | ✅ 2026-04-23 |
| 유저 식별 | `userId` (숫자 ID) | `userName` (displayName) | ✅ 2026-04-23 |
| 채널 ID 파라미터 | LLM이 `channelId` 제공 | context에서만 가져옴 | ✅ 2026-04-23 |

---

## 8. 대화 이력 저장 구조 (ChatMessage 확장)

Tool 호출 이력을 `conversation_contexts`에 저장해 세션 재시작 시 완전한 컨텍스트 복원.

### `Role` 확장

```csharp
public enum Role
{
    System,
    User,
    Assistant,
    ToolCall,      // LLM의 FunctionCall 요청 (model 역할)
    ToolResponse   // Tool 실행 결과 (user 역할, Gemini API 규약)
}
```

### `ChatMessage` 확장

```csharp
public record ChatMessage
{
    public Role Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? SenderName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    // Tool 전용 필드
    /// <summary>Tool 호출 식별자. ToolCall/ToolResponse 쌍 연결에 사용.</summary>
    public Guid? CallId { get; init; }
    /// <summary>Tool 이름. Role.ToolCall, Role.ToolResponse에서 사용.</summary>
    public string? ToolName { get; init; }
    /// <summary>Tool 호출 인자. Role.ToolCall에서만 사용. JSON string으로 저장 (타입 정보 보존).</summary>
    public string? ToolArgsJson { get; init; }

    /// <summary>
    /// Provider별 보조 메타데이터 JSON. Gemini의 thought_signature 등 텍스트로 표현 못하는
    /// model 응답 Part 데이터 보존용. Role.Assistant 또는 Role.ToolCall에 설정.
    /// </summary>
    public string? ProviderMetadataJson { get; init; }
}
```

### DB 저장 완전성 계약

**모든 ToolCall·ToolResponse는 세션 재시작 시 완전히 복원 가능한 상태로 저장되어야 한다.**

| 필드 | 저장 규칙 | 근거 |
|------|----------|------|
| `ToolCall.toolArgsJson` | LLM이 전달한 전체 인자를 JSON 직렬화한 값 | `BuildContents` 복원 시 `FunctionCall.Args` 재구성에 사용 |
| `ToolResponse.content` | `ToolResult`의 **runtime 타입** 기준 직렬화 JSON | 파생 클래스 필드(`memberCount`, `displayName` 등) 반드시 포함 |
| `Assistant.providerMetadataJson` / `ToolCall.providerMetadataJson` | model 응답에 포함된 non-text/non-FunctionCall Part 전체를 JSON 배열로 직렬화 | thought_signature 등 thinking 모델 reasoning state 보존 — 누락 시 Gemini implicit cache 미스 및 thinking context 단절 발생 |

**구현 규칙 (GeminiProvider)**:
- `toolResultJson` 생성 시 `JsonSerializer.Serialize(toolResult)` 금지 — 선언 타입(`ToolResult`) 기준 직렬화로 파생 필드 누락됨
- 반드시 `JsonSerializer.Serialize(toolResult, toolResult.GetType())` 사용 — runtime 타입 보존

**위반 시 영향**: 세션 복원 후 `BuildContents`가 `FunctionResponse.Response`에 불완전한 JSON을 포함해 LLM이 이전 tool 결과를 잘못 인식할 수 있음.

---

### MongoDB 저장 예시

```json
// ToolCall 메시지
{
  "role": "ToolCall",
  "content": "",
  "callId": "8f3d2a1b-4e5c-4f6a-b7c8-9d0e1f2a3b4c",
  "toolName": "discord_timeout_user",
  "toolArgsJson": "{\"userId\":\"443360484716969985\",\"durationSeconds\":300}",
  "timestamp": "2026-04-23T18:30:00Z"
}

// ToolResponse 메시지
{
  "role": "ToolResponse",
  "content": "{\"success\":true,\"userId\":\"443360484716969985\",\"displayName\":\"Xeno\",\"durationSeconds\":300}",
  "callId": "8f3d2a1b-4e5c-4f6a-b7c8-9d0e1f2a3b4c",
  "toolName": "discord_timeout_user",
  "timestamp": "2026-04-23T18:30:01Z"
}
```

---

## 9. 구현 순서 (예정)

1. `ToolResult` 기반 레코드 + Tool별 Result 레코드 추가 (`Core/Models/`)
2. `ILLMTool.ExecuteAsync` 반환 타입 변경 (`string` → `ToolResult`)
3. `TimeoutUserTool`, `ListChannelUsersTool` 구현 변경
4. `GeminiProvider`: `callId` 생성, ToolResult JSON 직렬화, FunctionResponse 구성
5. `Role` enum 확장, `ChatMessage` 확장
6. `GeminiProvider.BuildContents`: `ToolCall`/`ToolResponse` role 처리 추가
7. `LLMResponse`: `ToolCallHistory` 필드 추가
8. `MessageHandler`: `ToolCallHistory` DB 저장
