# Execution Rule
1. You MUST use the preview tool (`preview_start`) to run the application. NEVER use Bash background commands to start the bot process.
2. To stop the application, use `preview_stop`. NEVER use `Stop-Process` or `taskkill`.

# Workflow
1. You MUST build plan before modify the code or documents.
2. You MUST get approve about plan before executing plan.
   - This rule applies WITHOUT EXCEPTION — including simple bug fixes, typo corrections, and minor changes.
   - Do NOT begin any implementation until the user explicitly approves (e.g. "진행", "proceed", "ok").
   - Diagnosing and explaining a problem is allowed without approval. Writing or editing code is NOT.
3. Each plan MUST include all of the following sections:
   - **수정 범위**: files and classes to be changed
   - **사이드 이펙트**: potential impacts on other components, DI registrations, or behavior changes
   - **테스트 코드 추가 방법**: what tests to add, which paths to cover, and how
   - **문서 수정 계획**: which docs files to update and what to record
4. You SHOULD write work history in related document which in `docs` directory about that you work.
5. You MUST create a git commit at each of the following checkpoints:
   - When a single feature implementation is complete (all code + tests passing).
   - When a Phase or Step defined in the plan is fully done.
   - Commit message MUST follow Conventional Commits format and summarize what was completed.
   - Do NOT batch multiple phases or unrelated changes into one commit.
6. You CANNOT decide or estimate any functional specification or work. You MUST request question to user to decide ambiguous things.

# Security Rule
1. You MUST NOT read secret or local configuration files: `appsettings.*.json` (e.g. `appsettings.Local.json`, `appsettings.Production.json`, `appsettings.Secret.json`), `.env`, or any file containing credentials, tokens, or API keys. These files contain sensitive information and must never be read or exposed.
2. To diagnose configuration issues, read only the non-secret config files (e.g. `appsettings.json`) and the relevant Options class comments.

# Code Style
1. You SHOULD follow recommended naming rule from each framework.
2. All newly written or updated repository documents MUST be written in Korean unless the user explicitly asks for another language.

# Coding Rules
1. Don't just fix the visible symptoms of the problem; get to the root cause.
2. You MUST NOT unilaterally decide any spec or function without user approval.
3. You MUST ask to user if any functional specification is ambigious.
4. You MUST NOT silently introduce fallback behavior (e.g. default values, alternate code paths, retry-on-error, capability-based branching) without explicit user approval. Fallback is a functional spec decision, not an implementation detail.
5. You MUST NOT swallow or consume exceptions (empty `catch`, catch-and-log-only, catch-and-return-default, broad `catch { }`) unless the user has explicitly approved that specific exception as benign. Let exceptions propagate so the root cause stays visible during debugging.
6. When a failure mode is encountered at design or implementation time (missing data, unexpected state, upstream error, unsupported capability), you MUST stop, report the exact symptom and call site to the user, and ask how to handle it. Do NOT decide the recovery path on your own.
7. Existing fallback or catch-and-ignore code discovered during unrelated work MUST be reported to the user. Do NOT refactor or remove it unilaterally; flag it and wait for direction.

# Comment Rule
1. Every new class, record, interface, and major function or method MUST have a short purpose comment.
2. Non-trivial logic, branching, mapping, validation, and infrastructure wiring MUST include inline comments near the relevant block.
3. If a feature has an entry point or important flow, add a short feature-level comment in the related file.
4. Comments should explain intent, responsibility, and behavior clearly enough for future maintenance.

# Test Rule
1. New backend features MUST include automated tests unless there is a clear technical blocker.
2. Prefer unit tests for application and domain logic, and integration tests for API behavior and request/response flows.
3. Test structure SHOULD follow the same feature-first layout as the production code.
4. Coverage targets apply per module (Api, Application, Domain, Infrastructure) and ALL three metrics (Line, Branch, Method) must meet the threshold:

   | Suite | Line | Branch | Method |
   |---|---|---|---|
   | **Unit Tests** | ≥ 90% | ≥ 90% | ≥ 90% |
   | **Integration Tests** | ≥ 60% | ≥ 60% | ≥ 60% |
   | **Merged (Unit + Integration)** | ≥ 70% | ≥ 70% | ≥ 70% |

5. If coverage cannot be maintained for a change, explain the gap and the missing test scope in the final response.
6. Backend work MUST start from test design first, then failing tests, then implementation, then refactoring.
7. When adding or changing backend behavior, write or update the relevant tests before finalizing the production code.
8. After all plan execution and code changes are complete, you MUST run the test suite to verify nothing is broken.
9. If a change causes any module to drop below the targets in rule 4, you MUST add tests to restore coverage before the work is considered done.
