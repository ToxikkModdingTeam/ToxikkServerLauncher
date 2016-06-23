using System;
using System.Text;

namespace ToxikkServerLauncher
{
  public static class Utils
  {
    #region Write()
    /// <summary>
    /// writes a text to the console, using ^ + hexdigit as an escape sequence to change the output color. use ^^ to write a ^
    /// </summary>
    public static void Write(string text)
    {
      var buffer = new StringBuilder();
      for (int i = 0, len = text.Length; i < len; i++)
      {
        var c = text[i];
        if (c == '^')
        {
          if (++i >= len)
            break;

          var hex = "0123456789ABCDEF".IndexOf(char.ToUpper(text[i]));
          if (hex >= 0)
          {
            Console.Write(buffer);
            buffer.Clear();
            Console.ForegroundColor = (ConsoleColor)hex;
          }
          else if (text[i] == '^')
            buffer.Append('^');
        }
        else
          buffer.Append(c);
      }

      Console.Write(buffer);
      Console.ForegroundColor = ConsoleColor.Gray;
    }
    #endregion

    public static void WriteLine(string text)
    {
      Write(text + "\n");
    }
  }
}
