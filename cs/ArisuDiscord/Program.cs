
using ArisuDiscord.Bot;
using ArisuDiscord.Logger;
using Discord.Rest;

ArisuCore.Core.Logger = new Logger();
ArisuCore.Core.Initialize();

BotInterface bot = new BotInterface(ArisuCore.Core.Logger);
if (bot.IsInitialized == false)
{
    ArisuCore.Core.Logger.Error("Discord interface is not initialized!");
    return;
}
bot.OnMention += (sender, eventArgs) =>
{
    var task = ArisuCore.Core.SendMessage(eventArgs.UserMessage);
    task.Wait();
    string? response = task.Result;

    if (string.IsNullOrEmpty(response) == false)
    {
        eventArgs.ResponseMessage = response;
        eventArgs.IsSuccess = true;
    }
};

ArisuCore.Core.Logger.Info("Ready for messaging!");

Task.Delay(-1).Wait();