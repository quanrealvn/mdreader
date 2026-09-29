## Incident review: cache stampede, 14 September

### What happened

At 09:12 the cache entry for the pricing table expired. Every request that arrived in the
next four seconds missed the cache and went to the database, which reached its connection
limit at 09:12:06. Recovery took eleven minutes.

### Timeline

| Time  | Event                                 |
| ----- | ------------------------------------- |
| 09:12 | Pricing cache entry expires           |
| 09:13 | Database connection pool exhausted    |
| 09:17 | Rate limit applied at the edge        |
| 09:23 | Cache warm, error rate back to normal |

### Fix

```csharp
// One caller refreshes; the rest wait for it rather than piling on.
await _refreshLock.WaitAsync(cancellationToken);
```

### Follow-up

- [x] Single-flight refresh shipped
- [ ] Alert on connection-pool saturation
