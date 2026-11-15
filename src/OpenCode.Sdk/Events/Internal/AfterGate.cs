namespace OpenCode.Sdk.Internal;

/// <summary>Runs every deferred callback outside the gate, even when selection or another callback fails.</summary>
internal sealed class AfterGate
{
    private readonly List<Action> _items = [];
    private readonly List<Exception> _failures = [];

    public void Add(Action item) => _items.Add(item);

    public void RecordFailure(Exception exception) => _failures.Add(exception);

    public void Run(Action gateSection)
    {
        try
        {
            gateSection();
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
        }
        finally
        {
            Run();
        }
    }

    public void Run()
    {
        foreach (var item in _items)
        {
            try
            {
                item();
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
            }
        }

        if (_failures.Count != 0)
        {
            throw new AggregateException(_failures);
        }
    }
}
