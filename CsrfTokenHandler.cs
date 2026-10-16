using System.Net.Http.Json;

namespace PocketTasks;

public sealed class CsrfTokenHandler : DelegatingHandler
{
	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		if (request.Method != HttpMethod.Get
			&& request.Method != HttpMethod.Head
			&& request.Method != HttpMethod.Options
			&& request.Method != HttpMethod.Trace)
		{
			if (request.RequestUri is not { IsAbsoluteUri: true } requestUri)
				throw new InvalidOperationException("A base address is required to request an antiforgery token.");

			using var tokenRequest = new HttpRequestMessage(
				HttpMethod.Get,
				new Uri(requestUri, "/api/security/csrf-token"));
			using var tokenResponse = await base.SendAsync(tokenRequest, cancellationToken);
			tokenResponse.EnsureSuccessStatusCode();
			var tokenEnvelope = await tokenResponse.Content.ReadFromJsonAsync<CsrfTokenResponse>(
				cancellationToken: cancellationToken);
			if (string.IsNullOrWhiteSpace(tokenEnvelope?.RequestToken))
				throw new InvalidDataException("The server returned an empty antiforgery token.");

			request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", tokenEnvelope.RequestToken);
		}

		return await base.SendAsync(request, cancellationToken);
	}

	private sealed record CsrfTokenResponse(string RequestToken);
}
