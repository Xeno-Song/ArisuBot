# 문서 Index

| 파일 | 제목 | 설명 |
|------|------|------|
| [00_ARCHITECTURE.md](00_ARCHITECTURE.md) | 아키텍처 | 전체 시스템 구조, 기술 스택, 프로젝트 레이아웃 |
| [20_TOOL_SPEC.md](20_TOOL_SPEC.md) | LLM Tool 명세 | Tool 요청/응답 구조, GUID 관리, 인터페이스 설계, 구현 순서 |
| [50_LLM_INTERVIEW.md](50_LLM_INTERVIEW.md) | LLM 연동 인터뷰 | Gemini 연동 설계 결정 사항 및 확정 스펙 |
| [51_LOGGING_PLAN.md](51_LOGGING_PLAN.md) | 로깅 개선 계획 | MessageHandler·GeminiProvider 로그 추가 및 silent exception 개선 |
| [52_FALLBACK_MODEL_PLAN.md](52_FALLBACK_MODEL_PLAN.md) | Fallback 모델 설계 | Gemini ServerError 시 fallback 모델 자동 전환 설계 (미구현) |
| [53_THOUGHT_SIGNATURE_PRESERVATION.md](53_THOUGHT_SIGNATURE_PRESERVATION.md) | thought_signature 보존 | ProviderMetadataJson 도입으로 thinking 모델 reasoning state 영속화 |
| [54_EXPLICIT_CACHE_PLAN.md](54_EXPLICIT_CACHE_PLAN.md) | Explicit Cache 계획 | Gemini 명시적 캐싱 구현 계획. Phase 1(캐시 구현) + Phase 2(모니터링 사이드카) |
| [60_COMPACTION_EXPERIMENT.md](60_COMPACTION_EXPERIMENT.md) | Compaction 실험 | LLM 기반 대화 압축 접근법 실험 및 결과 |
| [61_COMPACTION_PLAN.md](61_COMPACTION_PLAN.md) | Compaction 정식 구현 | 트리거 3종, 새 session 생성, responseSchema, 슬래시 커맨드 |
| [62_MESSAGE_COALESCING.md](62_MESSAGE_COALESCING.md) | Message Coalescing | LLM 처리 중 수신 메시지 수집 후 일괄 처리, Typing 유지 |
| [63_DASHBOARD_PLAN.md](63_DASHBOARD_PLAN.md) | Dashboard 구현 계획 | Blazor Server 기반 독립 모니터링 Dashboard (Tool 제어, 에러 로그, 세션/토큰 조회) |
