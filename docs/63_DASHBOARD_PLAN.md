# Dashboard 구현 계획

## 개요

LLM 봇과 격리된 독립 프로세스로 실시간 상태 모니터링 Dashboard를 구성한다.
기존 콘솔 기반 `ArisuBot.Monitor` 사이드카를 대체하며, 웹 UI(Blazor Server)로 전환한다.

**목표 기능**:
- 도구(Tool) 활성화/비활성화 제어
- 실시간 처리 상태 (메시지 단위 + 도구 호출 단위)
- 도구 처리 에러 포함 전체 에러 로그
- 세션 목록 조회
- 총 토큰 사용량 집계

---

## 아키텍처

```
ArisuBot.Host
 ├── TCP 9876  →  이벤트 스트림 (단방향, 기존 유지)
 └── HTTP 9877 →  제어 API (신규: Tool on/off, 세션 조회)

ArisuBot.Dashboard  [신규]
 ├── TcpEventReceiver  →  TCP 9876 연결 (Monitor 대체)
 ├── DashboardStateService  →  in-memory 상태, Blazor notify
 ├── ControlApiClient  →  HTTP 9877 호출
 ├── MongoQueryService  →  MongoDB 직접 읽기 (히스토리)
 └── Blazor Pages: Index / Tools / Sessions / Errors / Tokens

ArisuBot.Monitor  →  코드 보존, 미실행
MongoDB  →  신규 컬렉션 error_logs
```

**격리 원칙**: Dashboard는 독립 프로세스. Host 장애 시 Dashboard는 "연결 끊김" 상태 표시 후 자동 재연결 시도. 모니터링 환경이 LLM 오류의 영향을 받지 않는다.

---

## 신규 이벤트 타입

기존 `LlmMonitorEvent` 계층에 5종 추가:

| 이벤트 | 필드 | 발생 시점 |
|--------|------|----------|
| `ProcessingStartedEvent` | ContextId, MessageCount | MessageHandler: Discord 메시지 처리 시작 |
| `ProcessingCompletedEvent` | ContextId, DurationMs | MessageHandler: 처리 완료 |
| `ToolCallStartedEvent` | ContextId, ToolName, ArgumentsJson | GeminiProvider: tool 실행 직전 |
| `ToolCallCompletedEvent` | ContextId, ToolName, DurationMs | GeminiProvider: tool 성공 반환 |
| `ToolCallFailedEvent` | ContextId, ToolName, ErrorMessage, DurationMs | GeminiProvider: tool 실패 |

---

## error_logs 컬렉션 스키마

```csharp
public class ErrorLogDocument
{
    public ObjectId Id { get; set; }
    public DateTime Timestamp { get; set; }      // UTC
    public ErrorType ErrorType { get; set; }      // LLMError / ToolError / SystemError / CacheError
    public string Source { get; set; }            // 클래스명 / 컴포넌트명
    public string Message { get; set; }           // 에러 메시지
    public string? Details { get; set; }          // 스택 트레이스 등 JSON
    public string? ContextId { get; set; }        // 연관 세션 ID (nullable)
}
```

**기록 시점**:
- `GeminiProvider`: ClientError(4xx), ServerError(5xx) 최종 실패
- `GeminiProvider`: tool `ExecuteAsync` catch
- `GeminiCacheManager`: 캐시 생성/갱신 실패

---

## Tool 비활성화 동작

- `IToolStateService.IsEnabled(name)` → false 시 `ToolResult.Fail("Tool is disabled")` 반환
- Tool declaration은 Gemini에 그대로 전달 → LLM은 호출 가능, Execute 단에서 거부
- disabled tool 호출 시 `ToolCallFailedEvent` emit (에러 로그 포함)
- 상태는 in-memory (재시작 시 초기화, 기본값: 모든 tool 활성화)

---

## Phase 계획

### Phase 1 — 에러 로그 인프라

**수정 범위**:

| 파일 | 변경 |
|------|------|
| `ArisuBot.Core/Interfaces/IErrorLogger.cs` | 신규 인터페이스: `LogAsync(ErrorLogEntry)` |
| `ArisuBot.Core/Models/ErrorLogEntry.cs` | 신규: ErrorType, Source, Message, Details, ContextId |
| `ArisuBot.Infrastructure/MongoDB/ErrorLogger.cs` | 신규: error_logs 컬렉션 저장 구현 |
| `ArisuBot.Infrastructure/MongoDB/Documents/ErrorLogDocument.cs` | 신규 |
| `ArisuBot.LLM/Gemini/GeminiProvider.cs` | 기존 catch 블록에 `IErrorLogger.LogAsync` 추가 |
| `ArisuBot.LLM/Gemini/GeminiCacheManager.cs` | 캐시 실패 시 `IErrorLogger.LogAsync` 추가 |
| `ArisuBot.Host/Program.cs` | `IErrorLogger` 싱글턴 DI 등록 |

**사이드 이펙트**:
- GeminiProvider·GeminiCacheManager에 `IErrorLogger` 의존성 추가
- 기존 예외 전파 유지 (swallow 없음, 로깅만 추가)

**테스트 코드**:
- `ErrorLogger` 단위: MongoDB 저장 검증
- `GeminiProvider` 에러 경로: `IErrorLogger.LogAsync` 호출 여부 검증
- Integration: Host 기동 후 `/api/errors` 조회 (Phase 4 이후 연동)

**문서 수정**: `docs/` 에 별도 문서 불필요. 본 계획서 갱신.

---

### Phase 2 — 모니터 이벤트 확장

**수정 범위**:

| 파일 | 변경 |
|------|------|
| `ArisuBot.LLM/Monitoring/LlmMonitorEvent.cs` | 신규 이벤트 5종 추가 |
| `ArisuBot.LLM/Gemini/GeminiProvider.cs` | tool 단위 이벤트 emit |
| `ArisuBot.Discord/Handlers/MessageHandler.cs` | `ILlmMonitorServer` 주입, 메시지 단위 이벤트 emit |

**사이드 이펙트**:
- `MessageHandler` DI 변경 (생성자 추가)
- 기존 `LlmMonitorApp`(Monitor)은 미지원 이벤트 수신 시 무시 (switch default 패턴 이미 있음)

**테스트 코드**:
- `GeminiProvider`: tool 성공/실패 시 올바른 이벤트 emit 검증
- `MessageHandler`: `ProcessingStartedEvent`/`ProcessingCompletedEvent` emit 검증

---

### Phase 3 — Tool 상태 관리

**수정 범위**:

| 파일 | 변경 |
|------|------|
| `ArisuBot.Core/Interfaces/IToolStateService.cs` | 신규: `IsEnabled`, `SetEnabled`, `GetAll` |
| `ArisuBot.LLM/Services/ToolStateService.cs` | 신규: `ConcurrentDictionary<string, bool>` in-memory |
| `ArisuBot.LLM/Gemini/GeminiProvider.cs` | tool Execute 전 `IsEnabled` 체크 추가 |
| `ArisuBot.Host/Program.cs` | `IToolStateService` 싱글턴 DI 등록 |

**사이드 이펙트**:
- `GeminiProvider`에 `IToolStateService` 의존성 추가
- disabled tool 호출 시 `ToolCallFailedEvent` + `IErrorLogger.LogAsync(ToolError)` 연계

**테스트 코드**:
- `ToolStateService` 단위: 기본값 true, `SetEnabled` 후 `IsEnabled` 검증
- `GeminiProvider` 통합: disabled tool 호출 시 Fail 반환 + 이벤트 emit 검증

---

### Phase 4 — Host HTTP Control API

**수정 범위**:

| 파일 | 변경 |
|------|------|
| `ArisuBot.Host/Program.cs` | `WebApplication` 전환, Kestrel HTTP 추가 |
| `ArisuBot.Host/Api/ToolsApi.cs` | 신규: minimal API 라우트 등록 |
| `ArisuBot.Host/Options/DashboardOptions.cs` | 신규: Host, Port (기본 9877) |
| `ArisuBot.Host/appsettings.json` | `Dashboard` 섹션 추가 |

**API 엔드포인트**:

```
GET  /api/tools                       → [{name, enabled}]
PUT  /api/tools/{name}                → body: {enabled: bool}
GET  /api/sessions                    → 최근 N개 세션 요약
GET  /api/sessions/{id}/tokens        → 세션 토큰 합계
```

**사이드 이펙트**:
- Host가 `GenericHost` → `WebApplication` 전환 필요
- 기존 `IHostedService` 등록 전부 유지
- 포트 충돌 없음 (9876 TCP, 9877 HTTP)
- 인증 없음 (내부망 전용)

**테스트 코드**:
- `WebApplicationFactory<Program>` 기반 통합 테스트
- `GET /api/tools` 200, tool 목록 검증
- `PUT /api/tools/{name}` → `GET` 재확인 (상태 변경 검증)

---

### Phase 5 — ArisuBot.Dashboard 프로젝트

**수정 범위**:

| 파일/디렉토리 | 변경 |
|---|---|
| `src/ArisuBot.Dashboard/` | 신규 Blazor Server 프로젝트 (.NET 8) |
| `src/ArisuBot.Dashboard/Services/TcpEventReceiver.cs` | IHostedService, TCP 9876 연결·파싱·재연결 |
| `src/ArisuBot.Dashboard/Services/DashboardStateService.cs` | in-memory 상태 + Blazor notify (`Action` 이벤트) |
| `src/ArisuBot.Dashboard/Services/ControlApiClient.cs` | HttpClient → Host 9877 API 호출 |
| `src/ArisuBot.Dashboard/Services/MongoQueryService.cs` | MongoDB 직접 읽기 (세션 히스토리, 에러 로그) |
| `src/ArisuBot.Dashboard/Pages/Index.razor` | 개요: 활성 세션, 처리 상태, 최근 이벤트 |
| `src/ArisuBot.Dashboard/Pages/Tools.razor` | 툴 목록 + on/off 토글 |
| `src/ArisuBot.Dashboard/Pages/Sessions.razor` | 세션 목록, 토큰 합계 |
| `src/ArisuBot.Dashboard/Pages/Errors.razor` | 에러 로그 테이블 (실시간 + 히스토리) |
| `src/ArisuBot.Dashboard/Pages/Tokens.razor` | 전체 토큰 집계 (모델별, 세션별) |
| `ArisuBot.sln` | 프로젝트 추가 |

**데이터 흐름**:
```
TCP 이벤트  →  TcpEventReceiver  →  DashboardStateService  →  Blazor (InvokeAsync StateHasChanged)
HTTP 제어   →  ControlApiClient  →  Host API  →  ToolStateService
MongoDB     →  MongoQueryService  →  Sessions / Errors / Tokens 페이지
```

**사이드 이펙트**:
- Dashboard와 Host가 동일 MongoDB를 공유 읽기 (Dashboard는 읽기 전용)
- Host 장애 시 TCP 재연결 루프 동작, Dashboard는 "연결 끊김" 표시 후 유지

**테스트 코드**:
- `TcpEventReceiver`: 이벤트 JSON 파싱 → `DashboardStateService` 업데이트 검증
- `ControlApiClient`: MockHttpMessageHandler로 API 호출 검증
- `MongoQueryService`: Mock MongoDB로 쿼리 검증

---

## Phase 실행 순서

```
Phase 1 (에러 로그 인프라)
Phase 2 (이벤트 확장)         ← Phase 1과 병렬 가능
Phase 3 (Tool 상태 관리)       ← Phase 1, 2 완료 후
Phase 4 (HTTP Control API)    ← Phase 3 완료 후
Phase 5 (Dashboard 프로젝트)   ← Phase 4 완료 후
```

---

## 커밋 체크포인트

| 체크포인트 | 내용 |
|-----------|------|
| Phase 1 완료 | feat(infra): error_logs MongoDB 컬렉션 + IErrorLogger 구현 |
| Phase 2 완료 | feat(monitor): ProcessingStarted/Completed, ToolCall* 이벤트 추가 |
| Phase 3 완료 | feat(llm): IToolStateService + Tool 비활성화 제어 |
| Phase 4 완료 | feat(host): HTTP Control API (도구 제어, 세션 조회) |
| Phase 5 완료 | feat(dashboard): Blazor Server Dashboard 초기 구현 |

---

## 미결 사항

없음. 모든 기능 범위 확정.
