namespace ArisuBot.Core.Models;

/// <summary>툴 실행 시 전달되는 Discord 컨텍스트. 툴이 서버/채널 정보를 참조할 때 사용.</summary>
/// <param name="GuildId">메시지가 전송된 Discord 서버 ID.</param>
/// <param name="ChannelId">메시지가 전송된 Discord 채널 ID.</param>
public record LLMToolExecutionContext(ulong GuildId, ulong ChannelId);
