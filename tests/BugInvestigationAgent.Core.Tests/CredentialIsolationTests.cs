using BugInvestigationAgent.Core.Context;
using BugInvestigationAgent.Core.Credentials;

namespace BugInvestigationAgent.Core.Tests;

public class SessionCredentialStoreTests
{
    [Test]
    public void Credentials_are_isolated_by_investigation_and_environment()
    {
        var store = new InMemorySessionCredentialStore();
        var inv1 = Guid.NewGuid();
        var inv2 = Guid.NewGuid();
        store.Set(inv1, "KMSRC", new CredentialSecret { Username = "rc-user", Password = "rc-pass" });

        Assert.That(store.Has(inv1, "KMSRC"), Is.True);
        Assert.That(store.Has(inv1, "kmsrc"), Is.True, "environment ids are case-insensitive");
        Assert.That(store.Has(inv1, "KMSQA"), Is.False, "never interchangeable between environments");
        Assert.That(store.Has(inv2, "KMSRC"), Is.False, "never shared between investigations");
        Assert.That(store.Take(inv1, "KMSQA"), Is.Null);
    }

    [Test]
    public void Take_removes_the_secret_from_memory()
    {
        var store = new InMemorySessionCredentialStore();
        var inv = Guid.NewGuid();
        store.Set(inv, "KMSQA", new CredentialSecret { Username = "u", Password = "p" });

        var taken = store.Take(inv, "KMSQA");

        Assert.That(taken!.Password, Is.EqualTo("p"));
        Assert.That(store.Has(inv, "KMSQA"), Is.False);
        Assert.That(store.Take(inv, "KMSQA"), Is.Null);
    }

    [Test]
    public void Clear_drops_every_secret_of_the_investigation_only()
    {
        var store = new InMemorySessionCredentialStore();
        var inv = Guid.NewGuid();
        var other = Guid.NewGuid();
        store.Set(inv, "KMSQA", new CredentialSecret { Username = "u", Password = "p" });
        store.Set(inv, "KMSRC", new CredentialSecret { Username = "u", Password = "p" });
        store.Set(other, "KMSQA", new CredentialSecret { Username = "x", Password = "y" });

        store.Clear(inv);

        Assert.That(store.Has(inv, "KMSQA") || store.Has(inv, "KMSRC"), Is.False);
        Assert.That(store.Has(other, "KMSQA"), Is.True);
    }

    [Test]
    public void Secret_ToString_never_prints_the_password()
    {
        var secret = new CredentialSecret { Username = "u", Password = "Sup3rSecret!" };
        Assert.That(secret.ToString(), Does.Not.Contain("Sup3rSecret!"));
    }

    [Test]
    public void Runtime_context_has_no_password_fields()
    {
        // The context is what skills (and the LLM prompts built from it) see — it must never carry secrets.
        var names = typeof(RuntimeContext).GetProperties().Select(p => p.Name.ToLowerInvariant());
        Assert.That(names.Any(n => n.Contains("password") || n.Contains("secret") || n.Contains("token")), Is.False);
    }
}
