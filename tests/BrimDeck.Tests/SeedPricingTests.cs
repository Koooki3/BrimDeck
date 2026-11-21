using BrimDeck.Core;

// ByteDance Seed names are written with dotted and with dashed versions, and TRAE adds a month and day ("Seed-2.1-Pro-0915");
// all of them take the catalog price of the same model (Pricing.LookupKey, Pricing.Find).
static class SeedPricingTests
{
    public static void Run(string root, Action<string, bool> check)
    {
        var folder = Path.Combine(root, "seed-prices");
        Directory.CreateDirectory(folder);
        TokenPrices Price(int n) => new(n * .000001m, n * .000005m, n * .0000001m, null, null);
        var catalog = new (string Id, int N)[]
        {
            ("bytedance-seed/seed-2-1-turbo", 1), ("bytedance-seed/seed-2.0-lite", 2), ("bytedance-seed/seed-2.0-mini", 3),
            ("bytedance-seed/seed-2-1-pro-1203", 4), ("acme/model-2-1", 5)
        };
        File.WriteAllText(Path.Combine(folder, "model-prices.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            updatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            models = catalog.Select(x => new ModelPrice(x.Id, Price(x.N), [])).ToArray()
        }));
        using var pricing = new Pricing(folder);
        var entry = new TokenEntry("trae:seed", DateTimeOffset.Now, "", 1000, 2000, 0, 0, 100);
        decimal Cost(int n) { var p = Price(n); return 1000 * p.Input + 100 * p.Output + 2000 * p.CacheRead!.Value; }
        bool All(int n, params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) == Cost(n));
        bool None(params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) is null);

        check("Seed versions match written with a dot or a dash, with or without the vendor",
            All(1, "Seed-2.1-Turbo", "seed-2-1-turbo", "bytedance-seed/seed-2.1-turbo") && All(2, "Seed-2.0-Lite", "seed-2-0-lite") && All(3, "Seed-2.0-Mini"));
        check("A Seed name with a month and day takes the price of its base model",
            All(1, "Seed-2.1-Turbo-0915") && pricing.Find("Seed-2.1-Turbo-0915")?.Model == "bytedance-seed/seed-2-1-turbo");
        check("A Seed name with its own dated catalog entry keeps that entry", All(4, "Seed-2.1-Pro-1203"));
        check("A Seed model that the catalog does not list stays unpriced", None("Seed-Code", "Seed-2.2-Turbo", "Seed-2.1-Turbo-1399"));
        // Seed 2.1 Pro is built in at twice the dollar prices of Seed 2.1 Turbo: 1, 0.2 and 5 USD per million input, cache-hit and output tokens.
        bool Pro(params string[] names) => names.All(name => pricing.Estimate(entry with { Model = name }) == 1000 * .000001m + 2000 * .0000002m + 100 * .000005m);
        check("Seed 2.1 Pro takes its built-in price under every spelling, and a cache write costs the input price",
            Pro("Seed-2.1-Pro", "Seed-2.1-Pro-0915", "seed-2-1-pro")
            && pricing.Estimate(entry with { Model = "Seed-2.1-Pro", Input = 0, CacheRead = 0, CacheWrite = 1000, Output = 0 }) == 1000 * .000001m);
        check("The models TRAE calls take the Seed price without the name of the app that serves them",
            Pro("Doubao-Seed-2.1-Pro", "doubao-seed-2-1-pro") &&All(1, "Doubao-Seed-2.1-Turbo", "Dola-Seed-2.1-Turbo") && None("Doubao-Seed-Code", "Doubao-Seed-Evolving"));
        // Seed 2.0 Code is built in at BytePlus's prices: 0.5, 0.1 and 3 USD per million tokens up to 128K input tokens, 1, 0.2 and 6 above.
        var large = entry with { Input = 100_000, CacheRead = 50_000, Output = 1_000 };
        check("Seed 2.0 Code takes its built-in price, tiered by the input of each request",
            new[] { "Dola-Seed-2.0-Code", "Doubao-Seed-2.0-Code", "seed-2-0-code" }.All(name =>
                pricing.Estimate(entry with { Model = name }) == 1000 * .0000005m + 2000 * .0000001m + 100 * .000003m
                && pricing.Estimate(large with { Model = name }) == 100_000 * .000001m + 50_000 * .0000002m + 1_000 * .000006m));
        check("Other names keep their exact form", None("acme/model-2.1", "model-2.1", "doubao-model-2.1"));
        check("A manual Seed price wins over the built-in one and applies to its dated name",
            pricing.TrySetManual("Seed-2.1-Pro", Price(6), true, out _) && All(6, "Seed-2.1-Pro-0915", "seed-2-1-pro"));
        check("A MaaS Seed name takes the catalog price", All(1, "maas-seed-2-1-turbo-volcengine-codeagent"));
    }
}
