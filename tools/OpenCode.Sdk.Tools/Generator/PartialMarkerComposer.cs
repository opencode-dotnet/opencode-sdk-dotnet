using System.Globalization;
using System.Text;
using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator;

internal static class PartialMarkerComposer
{
    /// <summary>
    /// A model with no family prefix needs an owner row before it can be placed: a refresh that
    /// brings one stops here until the row, with its reason, decides the model's folder.
    /// </summary>
    internal static void RefuseUnplacedModels(ModelPlacement placement)
    {
        var errors = new BindingErrorCollector();
        foreach (var model in placement.Unplaced)
        {
            errors.Add(
                BindingErrorCategory.Curation,
                model,
                "the model has no family prefix and no modelOwners row: add a row naming its owner, with its reason");
        }

        errors.ThrowIfAny();
    }

    /// <summary>
    /// A decline states that a wall stands over the operation. The probe binds it through the real
    /// selection path, so a bindable mark means the wall the row cites is gone — an upstream fix or
    /// a generator mechanism landed — and the decision has to be taken again rather than inherited.
    /// </summary>
    internal static void RefuseBindableDeclines(IReadOnlyList<PendingOperationMark> declinedMarks)
    {
        var errors = new BindingErrorCollector();
        foreach (var mark in declinedMarks.Where(static mark => mark.IsBindable))
        {
            errors.Add(
                BindingErrorCategory.Curation,
                mark.OperationId,
                "a bindable operation cannot be declined — select it or leave it pending");
        }

        errors.ThrowIfAny();
    }

    internal static string Compose(IReadOnlyList<string> selected, IReadOnlyList<PendingOperationMark> pendingMarks,
        IReadOnlyList<DeclinedOperationPlan> declined, IReadOnlyList<PendingOperationMark> declinedMarks,
        IReadOnlyList<string> transportOwned)
    {
        // The headline is the packing wall's plain-language half: while an operation is pending,
        // nobody has decided what to do about it, and a package would ship that silence.
        var content = new StringBuilder()
            .AppendLine(pendingMarks.Count > 0
                ? "Generation is incomplete; packages must not be published."
                : "Generation is complete at the declared coverage; packages may be published.")
            .Append("Selected operations: ")
            .AppendLine(selected.Count.ToString(CultureInfo.InvariantCulture))
            .Append("Pending operations: ")
            .AppendLine(pendingMarks.Count.ToString(CultureInfo.InvariantCulture))
            .Append("Declined operations: ")
            .AppendLine(declined.Count.ToString(CultureInfo.InvariantCulture))
            .Append("Transport-owned operations: ")
            .AppendLine(transportOwned.Count.ToString(CultureInfo.InvariantCulture));
        AppendOperations(content, "Selected", selected);
        AppendOperations(content, "Pending", pendingMarks.Select(FormatPendingLine));
        AppendOperations(content, "Declined", FormatDeclinedLines(declined, declinedMarks));
        AppendOperations(content, "Transport-owned", transportOwned.Select(FormatTransportOwnedLine));

        return content.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendOperations(StringBuilder content, string heading, IEnumerable<string> operationIds)
    {
        _ = content.Append(heading).AppendLine(":");
        foreach (var operationId in operationIds)
        {
            _ = content.Append("- ").AppendLine(operationId);
        }
    }

    private static string FormatPendingLine(PendingOperationMark mark) => $"{mark.OperationId} {FormatBindability(mark)}";

    private static string FormatBindability(PendingOperationMark mark) =>
        mark.IsBindable ? "[bindable]" : $"[refused: {mark.RefusalMessage}]";

    /// <summary>
    /// Each declined line carries the decision and the wall it rests on, so the reason and the
    /// verbatim refusal the binder finds today are reviewed against each other in one place.
    /// </summary>
    private static IEnumerable<string> FormatDeclinedLines(IReadOnlyList<DeclinedOperationPlan> declined,
        IReadOnlyList<PendingOperationMark> declinedMarks)
    {
        var marksById = declinedMarks.ToDictionary(static mark => mark.OperationId, StringComparer.Ordinal);
        return declined.Select(operation =>
            $"{operation.OperationId} [declined: {operation.Reason}] {FormatBindability(marksById[operation.OperationId])}");
    }

    /// <summary>The curation validator has already proven the row's subtree fingerprint on this bind.</summary>
    private static string FormatTransportOwnedLine(string operationId) => $"{operationId} [fingerprint-pinned]";
}
