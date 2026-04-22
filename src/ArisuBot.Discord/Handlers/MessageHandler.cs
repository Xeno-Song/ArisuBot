using ArisuBot.Discord.Options;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArisuBot.Discord.Handlers;

/// <summary>Discord 메시지 이벤트를 수신하고 응답 여부를 판단한다. LLM 연동은 미구현.</summary>
public class MessageHandler
{
    private readonly DiscordSocketClient _client;
    private readonly MessageListenerOptions _listenerOptions;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        IOptions<DiscordOptions> options,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _listenerOptions = options.Value.MessageListener;
        _logger = logger;
    }

    /// <summary>메시지 수신 시 호출. 봇 메시지 무시, 응답 여부 판단 후 처리.</summary>
    public async Task HandleAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage userMessage) return;

        var isMention = userMessage.MentionedUsers.Any(u => u.Id == _client.CurrentUser?.Id);

        if (!ShouldRespond(_listenerOptions, userMessage.Channel.Id, isMention)) return;

        // TODO: LLM 연동 미구현 — ConversationService + ILLMProvider 연동 예정
        _logger.LogInformation(
            "Message received in channel {ChannelId} from {UserId}",
            userMessage.Channel.Id, userMessage.Author.Id);

        // 임시 echo — LLM 연동 전 Discord 왕복 검증용. LLM 구현 후 제거.
        // 멘션 시 reply, 채널 메시지 시 일반 메시지로 응답.
        var reference = isMention ? new MessageReference(userMessage.Id) : null;
        await userMessage.Channel.SendMessageAsync($"[Echo] {userMessage.Content}", messageReference: reference);
    }

    /// <summary>채널 목록 포함 여부 또는 멘션 설정에 따라 응답 여부를 반환한다.</summary>
    internal static bool ShouldRespond(MessageListenerOptions options, ulong channelId, bool isMention)
    {
        if (options.ChannelIds.Contains(channelId)) return true;
        if (isMention && options.RespondToMentions) return true;
        return false;
    }
}
