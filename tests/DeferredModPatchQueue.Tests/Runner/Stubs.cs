using System.Collections.Generic;

namespace Godot
{
    public class Node { }
    public class Control : Node { }
}

namespace STS2Mobile
{
    internal static class PatchHelper
    {
        internal static readonly List<string> Messages = new();

        public static void Log(string message)
        {
            Messages.Add(message);
            var newline = message?.IndexOf('\n') ?? -1;
            System.Console.WriteLine(newline < 0 ? message : message.Substring(0, newline));
        }
    }
}
