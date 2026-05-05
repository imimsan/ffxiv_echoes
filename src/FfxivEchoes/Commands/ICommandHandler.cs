namespace FfxivEchoes.Commands;

public interface ICommandHandler
{
    string Verb { get; }
    string Usage { get; }
    string Description { get; }

    void Execute(string args);
}
