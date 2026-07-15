---
title: Thread CancellationToken through every async call, not just the last one
date: 2026-07-15
related: [../../architecture/resiliency.md, ../../architecture/gateway-architecture.md]
---

## Pattern

ASP.NET Core gives every controller action an implicit
`CancellationToken` tied to the request (available via the action
parameter or `HttpContext.RequestAborted`). A common gotcha: it gets
passed to the *first* `await` in a handler (say, a validation step or the
first DB call) and then silently dropped for subsequent calls — including,
critically, the outbound `HttpClient` call to the Account Service. The
token still exists and is still correct; it just isn't threaded all the
way through, so cancellation stops propagating partway down the call
chain.

This matters more than usual in this system specifically because of the
Polly timeout pipeline described in
[../../architecture/resiliency.md](../../architecture/resiliency.md):
Polly's timeout strategy works by cancelling the wrapped operation via a
`CancellationToken` it creates internally, composed with whatever token is
passed in. If the call site doesn't pass the request's token into the
Polly-wrapped call at all, cancellation still works for Polly's own
timeout — but a client that disconnects early (or an upstream timeout)
won't cancel the in-flight Account Service call the way it should, and the
Gateway keeps working on a call nobody is waiting for anymore.

## Guidance

Every method in the call chain from controller action down to the
`HttpClient.SendAsync`/`PostAsJsonAsync` call — application handler,
repository method, the Polly-wrapped HTTP call — should accept a
`CancellationToken` parameter and pass it forward, not just at the first
hop. This includes EF Core calls (`SaveChangesAsync(cancellationToken)`,
`FirstOrDefaultAsync(predicate, cancellationToken)`) as well as the
outbound HTTP call.

A quick way to catch a dropped token during review: grep the diff for
`async Task` method signatures that don't end in a `CancellationToken`
parameter, and for `await` calls that pass no token where a token
parameter was available in scope. Neither is proof of a bug on its own,
but both are worth a second look in a method that's meant to be
cancellable.

## Examples

**Wrong** — token available but not threaded past the first call:

```csharp
public async Task<EventRecord> SubmitEventAsync(EventRequest request, CancellationToken ct)
{
    var existing = await _repository.FindByEventIdAsync(request.EventId, ct); // token used here
    if (existing is not null) return existing;

    var response = await _accountServiceClient.ApplyTransactionAsync(request); // dropped here
    return await _repository.InsertAsync(request, response);                  // and here
}
```

**Right** — threaded through every async call in the chain:

```csharp
public async Task<EventRecord> SubmitEventAsync(EventRequest request, CancellationToken ct)
{
    var existing = await _repository.FindByEventIdAsync(request.EventId, ct);
    if (existing is not null) return existing;

    var response = await _accountServiceClient.ApplyTransactionAsync(request, ct);
    return await _repository.InsertAsync(request, response, ct);
}
```
