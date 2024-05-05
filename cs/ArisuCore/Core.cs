using ArisuCore.Config;
using ArisuCore.Log;
using Discord.WebSocket;

namespace ArisuCore
{
    public class Core
    {
        public static ILogger? Logger { get; set; }

        public static async void Run()
        {
            var config = ConfigManager.LoadDiscordConfig();
            if (config == null)
            {
                Logger?.Fatal("Cannot load discord config.");
                return;
            }

            Logger?.Info($"Discord token : {config.Token}");

            var _client = new DiscordSocketClient();

            await _client.LoginAsync(Discord.TokenType.Bot, config.Token);
            await _client.StartAsync();

            Logger?.Info($"Discord login state : {_client.LoginState}");

            await _client.LogoutAsync();
            //await Task.Delay(-1);
        }
    }
}
