namespace ColdChainQuotes;

public static class ShipmentQueries
{
    // Receiver: any sequence of shipments. Keeps one storage class, in input order.
    public static IEnumerable<Shipment> ByStorageClass(this IEnumerable<Shipment> shipments, StorageClass storageClass) =>
        shipments.Where(s => s.StorageClass == storageClass);
}

public static class ResultRules
{
    // Receiver: a Result. A success must satisfy the rule or it becomes ifRuleFails.
    // A failure passes through unchanged and the rule is never evaluated.
    public static Result<T> Require<T>(this Result<T> result, Func<T, bool> rule, AppError ifRuleFails) where T : notnull =>
        result.Bind(value => rule(value) ? Result<T>.Ok(value) : Result<T>.Fail(ifRuleFails));
}
