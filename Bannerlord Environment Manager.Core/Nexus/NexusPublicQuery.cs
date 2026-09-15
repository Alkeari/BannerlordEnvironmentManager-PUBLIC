namespace BannerlordEnvironmentManager.Core.Nexus;

// Nexus's v2 GraphQL API answers public questions, such as a mod page's current version, without any
// credential. Kept apart from INexusTransport, whose every call carries a key, so that nothing asked
// through this seam can ever be handed one.
public interface INexusPublicQuery
{
    Task<NexusHttpResponse> QueryAsync(string query, CancellationToken cancellationToken);
}
