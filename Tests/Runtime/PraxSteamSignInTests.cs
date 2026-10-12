using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Praxsuite.Tests
{
    /// <summary>
    /// Steam sign-in. What is worth pinning here is not the HTTP call - that needs a gateway -
    /// but the shape of the API, because the shape is the security property: a method that let a
    /// caller pass a Steam ID would look identical at the call site and mean something entirely
    /// different.
    /// </summary>
    public class PraxSteamSignInTests
    {
        private const string Workspace = "11111111-2222-3333-4444-555555555555";

        [Test]
        public void Ticket_route_is_the_providers_own_auth_route()
        {
            Assert.AreEqual(
                "https://gateway.praxsuite.com/" + Workspace + "/auth/steam/ticket",
                PraxRoutes.Auth(PraxRoutes.CloudHost, Workspace, "steam/ticket"));
        }

        [Test]
        public void Steam_sign_in_takes_no_player_id()
        {
            var parameters = typeof(PraxAuth)
                .GetMethod(nameof(PraxAuth.SignInWithSteamAsync), BindingFlags.Public | BindingFlags.Instance)
                .GetParameters()
                .Select(p => p.Name)
                .ToArray();

            // The id the player is signed in as comes back from Steam. If a parameter ever
            // appears here that a caller could fill with one, this test is the warning that the
            // method stopped being proof and became a label again.
            CollectionAssert.DoesNotContain(parameters, "steamId");
            CollectionAssert.DoesNotContain(parameters, "platformPlayerId");
            CollectionAssert.DoesNotContain(parameters, "userId");

            Assert.AreEqual("sessionTicket", parameters[0]);
        }

        [Test]
        public void Ticket_sign_in_takes_no_player_id_either()
        {
            var parameters = typeof(PraxAuth)
                .GetMethod(nameof(PraxAuth.SignInWithTicketAsync), BindingFlags.Public | BindingFlags.Instance)
                .GetParameters()
                .Select(p => p.Name)
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "providerSlug", "sessionTicket", "identity", "displayName", "metadata", "ct" },
                parameters);
        }

        [Test]
        public void Sdk_does_not_depend_on_steamworks()
        {
            // Getting the ticket is the game's job. A reference to a Steamworks assembly here
            // would force every project using the SDK to carry it, including the ones that never
            // ship on Steam.
            var referenced = typeof(PraxAuth).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name.ToLowerInvariant())
                .ToArray();

            Assert.IsFalse(referenced.Any(name => name.Contains("steam")),
                "The SDK must take the ticket as a string, not take a dependency on Steamworks.");
        }
    }
}
