using System.Diagnostics;

namespace ForgeDesk.UITests;

/// <summary>A realistic small git repository (Node project with history and pending changes).</summary>
public sealed class SampleProject : IDisposable
{
    public SampleProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "forgedesk-ui", "acme-web-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Root);
        Git("init", "-b", "main");
        Git("config", "user.name", "Ada Lovelace");
        Git("config", "user.email", "ada@example.com");

        Write("README.md", "# Acme Web\n\nThe storefront of Acme Corp, built with Vite and React.\n\n## Getting started\n\n```\nnpm install\nnpm run dev\n```\n");
        Write("package.json", """
            {
              "name": "acme-web",
              "version": "0.4.0",
              "private": true,
              "scripts": {
                "dev": "vite",
                "build": "tsc && vite build",
                "test": "vitest run",
                "lint": "eslint src",
                "format": "prettier --write ."
              },
              "dependencies": { "react": "^19.0.0", "react-dom": "^19.0.0" },
              "devDependencies": { "vite": "^7.0.0", "typescript": "^5.9.0", "vitest": "^4.0.0", "eslint": "^9.0.0", "prettier": "^3.0.0" }
            }
            """);
        Write(".gitignore", "node_modules/\ndist/\n");
        Write("src/main.tsx", "import { createRoot } from 'react-dom/client';\nimport { App } from './App';\n\ncreateRoot(document.getElementById('root')!).render(<App />);\n");
        Write("src/App.tsx", "export function App() {\n  // TODO: add the product grid\n  return <h1>Acme</h1>;\n}\n");
        Git("add", "-A");
        Git("commit", "-m", "feat: scaffold the storefront");

        Write("src/cart.ts", "export interface CartLine { sku: string; quantity: number; }\n\nexport function total(lines: CartLine[], prices: Map<string, number>): number {\n  return lines.reduce((sum, l) => sum + (prices.get(l.sku) ?? 0) * l.quantity, 0);\n}\n");
        Write("src/cart.test.ts", "import { total } from './cart';\n\ntest('total', () => {\n  expect(total([{ sku: 'a', quantity: 2 }], new Map([['a', 5]]))).toBe(10);\n});\n");
        Git("add", "-A");
        Git("commit", "-m", "feat(cart): compute cart totals");

        Write(".github/workflows/ci.yml", "name: CI\non: [push, pull_request]\njobs:\n  test:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n      - run: npm ci && npm test\n");
        Git("add", "-A");
        Git("commit", "-m", "ci: run tests on every push");
        Git("tag", "-a", "v0.4.0", "-m", "Release 0.4.0");

        // Pending work: one modified, one new file.
        Write("src/App.tsx", "import { total } from './cart';\n\nexport function App() {\n  // FIXME: prices should come from the API\n  return <h1>Acme — {total([], new Map())}</h1>;\n}\n");
        Write("src/checkout.ts", "export const checkoutUrl = '/checkout';\n");
    }

    public string Root { get; }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.Replace("\r\n", "\n"));
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
