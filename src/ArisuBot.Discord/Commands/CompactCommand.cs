using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Services;
using Discord.Interactions;
using Discord.WebSocket;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Commands;

/// <summary>
/// /compact 슬래시 커맨드. 현재 채널/DM 세션에 즉시 Compaction을 실행하고,
/// 성공 시 semantic memory 추출도 연속 실행한다.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Discord.Net SocketInteractionContext 실연결 필요. E2E 테스트 대상.")]
public class CompactCommand : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ConversationService      _conversationService;
    private readonly ICompactionService       _compactionService;
    private readonly ISemanticMemoryService   _semanticMemoryService;

    public CompactCommand(
        ConversationService conversationService,
        ICompactionService compactionService,
        ISemanticMemoryService semanticMemoryService)
    {
        _conversationService   = conversationService;
        _compactionService     = compactionService;
        _semanticMemoryService = semanticMemoryService;
    }

    /// <summary>현재 세션에 Compaction을 즉시 실행한다. Cooldown 조건 무시 — 강제 실행.</summary>
    [SlashCommand("compact", "현재 대화 세션을 압축(Compaction)합니다.")]
    public async Task ExecuteAsync()
    {
        // 지연 응답: Compaction LLM 호출이 3초 이상 걸릴 수 있음
        await DeferAsync(ephemeral: true);

        ArisuBot.Core.Models.ContextType contextType;
        ulong targetId;

        if (Context.Channel is SocketDMChannel)
        {
            contextType = ArisuBot.Core.Models.ContextType.User;
            targetId    = Context.User.Id;
        }
        else
        {
            contextType = ArisuBot.Core.Models.ContextType.Channel;
            targetId    = Context.Channel.Id;
        }

        var context = await _conversationService.GetContextAsync(targetId, contextType);

        if (context.Messages.Count == 0)
        {
            await FollowupAsync("대화 기록이 없어 Compaction을 실행할 수 없습니다.", ephemeral: true);
            return;
        }

        var newContext = await _compactionService.RunAsync(context);

        // Compaction 성공 후 semantic memory 추출 — 실패해도 compaction 결과는 유지
        var memoryStatus = "완료";
        try
        {
            await _semanticMemoryService.ExtractAndSaveAsync(context);
        }
        catch (Exception ex)
        {
            memoryStatus = $"실패 ({ex.Message})";
        }

        await FollowupAsync(
            $"Compaction 완료. 새 세션 ID: `{newContext.Id}`\nSemantic Memory 추출: {memoryStatus}",
            ephemeral: true);
    }
}
