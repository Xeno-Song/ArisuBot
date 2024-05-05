using ArisuCore.Config;
using ArisuCore.Log;
using Discord.Commands;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ArisuDiscord.Bot
{
    internal class BotInterface
    {
        private DiscordSocketClient? _client;
        private DiscordConfig? _config;
        private ILogger? _logger;

        public event EventHandler<MentionEventArgs>? OnMention;

        public bool IsInitialized { get; private set; } = false;

        public BotInterface(ILogger logger)
        {
            _logger = logger;
            _config = DiscordConfig.LoadConfig("../../../../../secret/discord_config.json");

            if (_config == null)
            {
                _logger.Fatal("Failed to load discord config.");
                return;
            }

            _logger.Info($"Discord token : {_config.Token}");

            _client = new DiscordSocketClient(new DiscordSocketConfig()
            {
                GatewayIntents = Discord.GatewayIntents.AllUnprivileged | Discord.GatewayIntents.MessageContent,
            });
            _client.MessageReceived += HandleMessageReceived;

            _client.LoginAsync(Discord.TokenType.Bot, _config.Token).Wait();
            if (_client.LoginState != Discord.LoginState.LoggedIn)
            {
                _logger.Error($"Failed to login discord bot. {_client.LoginState}");
                return;
            }
            _client.StartAsync().Wait();

            IsInitialized = true;
        }

        private async Task HandleMessageReceived(SocketMessage arg)
        {
            _logger?.Info($"Message received from channel {arg.Channel.Name}");
            var message = arg as SocketUserMessage;
            if (message == null || message.Author.IsBot) return;

            int argPos = 0;
            if (message.HasMentionPrefix(_client?.CurrentUser, ref argPos))
            {
                var context = new SocketCommandContext(_client, message);
                _logger?.Info($"Message received from {context.User.Username}");

                var typing_object = context.Channel.EnterTypingState();

                MentionEventArgs eventArgs = new MentionEventArgs();
                eventArgs.UserName = context!.User.Username;
                eventArgs.UserMessage = context!.Message.Content;

                OnMention?.Invoke(this, eventArgs);

                if (eventArgs.IsSuccess == false)
                {
                    typing_object.Dispose();
                    return;
                }

                var result = await context.Channel.SendMessageAsync(eventArgs.ResponseMessage);
                if (result == null)
                {
                    _logger?.Error("Failed to send message.");
                    await arg.AddReactionAsync(new Discord.Emoji("➿"));
                }
                typing_object.Dispose();
            }
            else
            {
                _logger?.Info($"Message doesn't have mention. {arg.Content}");
                // _logger?.Info($"Message doesn't have mention. {arg.Content}");
                // await arg.AddReactionAsync(new Discord.Emoji(""));
            }
        }
    }
}
