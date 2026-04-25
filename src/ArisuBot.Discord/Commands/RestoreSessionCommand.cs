using ArisuBot.Core.Models;
using ArisuBot.Core.Services;
using Discord.Interactions;
using Discord.WebSocket;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Commands;

/// <summary>/restore-session 슬래시 커맨드. 과거 세션을 MongoDB ObjectId로 복원한다.</summary>
[ExcludeFromCodeCoverage(Justification = "Discord.Net SocketInteractionContext 실연결 필요. E2E 테스트 대상.")]
public class RestoreSessionCommand : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ConversationService _conversationService;

    public RestoreSessionCommand(ConversationService conversationService)
    {
        _conversationService = conversationService;
    }

    /// <summary>
    /// sessionId로 과거 세션을 조회해 현재 채널/DM의 active session으로 복원한다.
    /// 보안: 조회된 세션의 targetId가 현재 채널/유저와 일치하지 않으면 오류 반환.
    /// </summary>
    [SlashCommand("restore-session", "과거 세션을 ID로 복원합니다.")]
    public async Task ExecuteAsync(
        [Summary("session_id", "복원할 세션의 MongoDB ObjectId")] string sessionId)
    {
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

        var newContext = await _conversationService.RestoreSessionAsync(sessionId, targetId, contextType);

        if (newContext is null)
        {
            await FollowupAsync(
                $"세션 복원 실패. ID `{sessionId}`를 찾을 수 없거나 현재 채널/유저 소유가 아닙니다.",
                ephemeral: true);
            return;
        }

        await FollowupAsync(
            $"세션 복원 완료. 새 세션 ID: `{newContext.Id}` (메시지 수: {newContext.Messages.Count})",
            ephemeral: true);
    }
}
