namespace ToxikkServerLauncher
{
  class Program
  {
    static int Main(string[] args)
    {
      var cli = new CLI();
      return cli.Run(args);
    }
  }
}
