using ArisuBot.Core.Interfaces;
using ArisuBot.Core.Models;
using ArisuBot.Core.Services;
using Discord.Interactions;
using Discord.WebSocket;
using System.Diagnostics.CodeAnalysis;

namespace ArisuBot.Discord.Commands;

/// <summary>/new-session 슬래시 커맨드. 대화 컨텍스트 초기화 및 프롬프트 재로드.</summary>
[ExcludeFromCodeCoverage(Justification = "Discord.Net SocketInteractionContext 실연결 필요. E2E 테스트 대상.")]
public class NewSessionCommand : InteractionModuleBase<SocketInteractionContext>
{
    private readonly ConversationService _conversationService;
    private readonly IPromptLoader _promptLoader;

    public NewSessionCommand(ConversationService conversationService, IPromptLoader promptLoader)
    {
        _conversationService = conversationService;
        _promptLoader = promptLoader;
    }

    /// <summary>현재 채널/DM 컨텍스트를 새 세션으로 교체하고 프롬프트를 파일에서 재로드한다.</summary>
    [SlashCommand("new-session", "대화를 초기화하고 프롬프트를 재로드합니다.")]
    public async Task ExecuteAsync()
    {
        // DM이면 User 컨텍스트, Guild 채널이면 Channel 컨텍스트
        ArisuBot.Core.Models.ContextType contextType;
        ulong targetId;

        if (Context.Channel is SocketDMChannel)
        {
            contextType = ArisuBot.Core.Models.ContextType.User;
            targetId = Context.User.Id;
        }
        else
        {
            contextType = ArisuBot.Core.Models.ContextType.Channel;
            targetId = Context.Channel.Id;
        }

        // 기존 세션은 히스토리로 보존하고 새 빈 세션 도큐먼트 생성
        await _conversationService.StartNewSessionAsync(targetId, contextType);

        // 프롬프트 재로드는 전역 적용 (모든 채널에서 새 프롬프트 사용)
        _promptLoader.Reload();

        await RespondAsync("세션이 초기화되었습니다.", ephemeral: true);
    }
}
