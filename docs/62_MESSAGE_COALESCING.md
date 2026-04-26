# Message Coalescing — 연속 메시지 일괄 처리

## 배경

Discord 메시지가 연속으로 수신될 경우 각 메시지가 독립적인 LLM 호출을 발생시켜 두 가지 문제가 발생했다.

1. **응답 Latency 증가**: 동시 LLM 요청이 경합하면서 응답 시간이 늘어남
2. **응답 품질 저하**: 각 메시지가 독립된 컨텍스트 기준으로 처리되어 연속 대화 맥락을 반영하지 못함

---

## 설계 목표

- LLM 처리 중 수신된 메시지를 수집하고, 현재 처리 완료 후 한 번에 처리한다.
- Typing 인디케이터를 배치 전환 시에도 끊기지 않고 유지한다.
- 컨텍스트(채널/DM)별로 독립 슬롯을 운영해 채널 간 처리가 서로 영향을 주지 않는다.

---

## 구현 — Coalescing 패턴

### 핵심 구성 요소

| 구성 요소 | 설명 |
|-----------|------|
| `ContextSlot` | 컨텍스트별 처리 상태. `IsProcessing` + `Pending` 목록 |
| `PendingMessage` | 라우팅 완료 후 대기 중인 메시지 정보 |
| `_slots` | `ConcurrentDictionary<string, ContextSlot>`. 키 = `{contextType}:{targetId}` |

### 처리 흐름

```
메시지 도착 (HandleAsync)
    ↓
슬롯 확인 (lock)
    ├─ IsProcessing = true → Pending에 추가, return
    └─ IsProcessing = false → IsProcessing = true, 처리 루프 시작 (Task.Run)

처리 루프 (RunProcessingLoopAsync)
    ├─ KeepTypingAsync 시작 (CancellationToken 기반, 루프 전체 유지)
    └─ while true:
           ProcessBatchAsync(batch)  ← LLM 1회 호출
           lock 슬롯:
               Pending 없음 → IsProcessing = false, return (정상 종료)
               Pending 있음 → 전체 드레인 → 새 batch, 반복

KeepTypingAsync (백그라운드)
    └─ TriggerTypingAsync() → delay 8초 → 반복 (취소 신호까지)
```

### 메시지 결합 포맷

| 컨텍스트 | 결합 방식 | 예시 |
|----------|-----------|------|
| 채널 (다수 발신자 가능) | `[이름]: 내용\n[이름]: 내용` | `[Alice]: 안녕\n[Bob]: 질문 있어요` |
| DM (항상 동일 발신자) | `내용\n내용` | `질문1\n질문2` |

---

## 주요 구현 파일

- `src/ArisuBot.Discord/Handlers/MessageHandler.cs`
  - `HandleAsync`: 진입점, 슬롯 등록 및 루프 시작
  - `RunProcessingLoopAsync`: 처리 루프, pending 드레인
  - `ProcessBatchAsync`: 실제 LLM 호출 (기존 HandleAsync 핵심 로직)
  - `CombineBatchContent`: 배치 메시지 결합 (`internal static`, 유닛 테스트 대상)
  - `KeepTypingAsync`: Discord typing 인디케이터 유지

---

## 동시성 안전성

- `ContextSlot`의 모든 상태 변경은 `lock (slot)` 보호 하에 수행
- `_slots`는 `ConcurrentDictionary` — 슬롯 생성 자체는 thread-safe
- 처리 루프의 정상 종료 경로(`return`)에서 `IsProcessing = false` 설정 후 반환하므로, `finally` 블록에서 이중 설정으로 인한 race condition 없음
- 예외 종료 경로에서는 `catch`에서 `lock(slot) { IsProcessing = false }` 명시 처리

---

## 테스트

- `tests/ArisuBot.Tests.Unit/Discord/MessageHandlerCoalescingTests.cs`
  - `CombineBatchContent` 채널/DM 포맷 검증
  - `ContextSlot` 초기 상태 및 상태 전환 검증
