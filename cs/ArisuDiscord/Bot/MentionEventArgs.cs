using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArisuDiscord.Bot
{
    internal class MentionEventArgs : EventArgs
    {
        public string UserName { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public bool IsSuccess { get; set; } = false;
        public string ResponseMessage { get; set; } = string.Empty;
    }
}
