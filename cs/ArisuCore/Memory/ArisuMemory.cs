using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ArisuCore.Memory
{
    internal enum Role
    {
        User,
        Assistant
    }

    internal class ChatHistory
    {
        public Role Sender { get; set; } = Role.User;
        public string Message { get; set; } = string.Empty;
    }

    internal class ArisuMemory
    {
        public static List<ChatHistory> History = new List<ChatHistory>();

        public static void AddMemory(Role sender, string message)
        {
            History.Add(new ChatHistory()
            {
                Sender = sender,
                Message = message
            });

            while (History.Count > 10)
            {
                History.Remove(History.First());
            }
            Console.WriteLine($"Saved History : {History.Count}");

            File.AppendAllText("../../../../../log/history.txt", $"{sender}: {message}");
        }
    }
}
