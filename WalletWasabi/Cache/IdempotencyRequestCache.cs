using Microsoft.Extensions.Caching.Memory;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Cache;

public class IdempotencyRequestCache
{
	public IdempotencyRequestCache(IMemoryCache cache)
	{
		ResponseCache = cache;
	}

	public delegate Task<TResponse> ProcessRequestDelegateAsync<TRequest, TResponse>(TRequest sender, CancellationToken cancellationToken);

	/// <summary>Timeout specifying how long a request response can stay in memory.</summary>
	private static MemoryCacheEntryOptions IdempotencyEntryOptions { get; } = new()
	{
		AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
	};

	private static TimeSpan RequestTimeout { get; } = TimeSpan.FromMinutes(5);

	/// <summary>Guards <see cref="ResponseCache"/>.</summary>
	/// <remarks>Unfortunately, <see cref="CacheExtensions.GetOrCreate{TItem}(IMemoryCache, object, Func{ICacheEntry, TItem})"/> is not atomic.</remarks>
	/// <seealso href="https://github.com/dotnet/runtime/issues/36499"/>
	private object ResponseCacheLock { get; } = new();

	/// <remarks>Guarded by <see cref="ResponseCacheLock"/>.</remarks>
	private IMemoryCache ResponseCache { get; }

	/// <typeparam name="TRequest">
	/// <see langword="record"/>s are preferred as <see cref="object.GetHashCode"/>
	/// and <see cref="object.Equals(object?)"/> are generated for <see langword="record"/> types automatically.
	/// </typeparam>
	/// <typeparam name="TResponse">Type associated with <typeparamref name="TRequest"/>. The correspondence should be 1:1 mapping.</typeparam>
	public Task<TResponse> GetCachedResponseAsync<TRequest, TResponse>(TRequest request, ProcessRequestDelegateAsync<TRequest, TResponse> action, CancellationToken cancellationToken = default)
		where TRequest : notnull
	{
		return GetCachedResponseAsync(request, action, IdempotencyEntryOptions, cancellationToken);
	}

	/// <typeparam name="TRequest">
	/// <see langword="record"/>s are preferred as <see cref="object.GetHashCode"/>
	/// and <see cref="object.Equals(object?)"/> are generated for <see langword="record"/> types automatically.
	/// </typeparam>
	/// <typeparam name="TResponse">Type associated with <typeparamref name="TRequest"/>. The correspondence should be 1:1 mapping.</typeparam>
	public async Task<TResponse> GetCachedResponseAsync<TRequest, TResponse>(TRequest request, ProcessRequestDelegateAsync<TRequest, TResponse> action, MemoryCacheEntryOptions options, CancellationToken cancellationToken)
		where TRequest : notnull
	{
		bool callAction = false;
		TaskCompletionSource<TResponse> responseTcs;

		lock (ResponseCacheLock)
		{
			if (!ResponseCache.TryGetValue(request, out responseTcs))
			{
				callAction = true;
				responseTcs = new();
				ResponseCache.Set(request, responseTcs, options);
			}
		}

		if (callAction)
		{
			using CancellationTokenSource timeoutCts = new(RequestTimeout);
			try
			{
				using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

				var result = await action(request, cancellationToken).WithAwaitCancellationAsync(linkedCts.Token).ConfigureAwait(false);
				responseTcs.SetResult(result);
				return result;
			}
			catch (Exception e)
			{
				lock (ResponseCacheLock)
				{
					ResponseCache.Remove(request);
				}

				responseTcs.SetException(!timeoutCts.IsCancellationRequested
					? e
					: new InvalidOperationException("DeadLock prevention timeout kicked in!", e));

				// The exception will be thrown below at 'await' to avoid unobserved exception.
			}
		}

		return await responseTcs.Task.ConfigureAwait(false);
	}

	/// <remarks>
	/// For testing purposes only.
	/// <para>Note that if there is a simultaneous request for the cache key, it is not stopped and its result is discarded.</para>
	/// </remarks>
	internal void Remove<TRequest>(TRequest cacheKey)
		where TRequest : notnull
	{
		lock (ResponseCacheLock)
		{
			ResponseCache.Remove(cacheKey);
		}
	}
}
