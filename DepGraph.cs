// Calculo de dependencias que quedan sin uso al desinstalar mods.
public static class DepGraph
{
    // Dependencias (transitivas) de `removing` que estan instaladas y que NINGUN otro mod instalado
    // necesita. Se itera hasta punto fijo: quitar A puede dejar sin uso a B, que solo A necesitaba.
    // depsOf: "Owner-Name" -> dependencias "Owner-Name" (sin version). Un mod que no aparece en el
    // mapa se asume sin dependencias.
    public static List<string> Unused(IEnumerable<string> removing, IEnumerable<string> installed,
        IReadOnlyDictionary<string, string[]> depsOf)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var inst = new HashSet<string>(installed, cmp);
        var gone = new HashSet<string>(removing, cmp);

        // Candidatas: cierre de dependencias de lo que se quita, solo las instaladas.
        var cand = new HashSet<string>(cmp);
        var stack = new Stack<string>(gone);
        while (stack.Count > 0)
        {
            if (!depsOf.TryGetValue(stack.Pop(), out var ds)) continue;
            foreach (var d in ds)
                if (inst.Contains(d) && !IsBepInEx(d) && !gone.Contains(d) && cand.Add(d))
                    stack.Push(d);
        }

        var removed = new HashSet<string>(gone, cmp);
        bool changed;
        do
        {
            changed = false;
            foreach (var c in cand.ToList())
            {
                if (removed.Contains(c)) continue;
                bool needed = inst.Any(m => m != c && !removed.Contains(m)
                    && depsOf.TryGetValue(m, out var ds) && ds.Contains(c, cmp));
                if (!needed) { removed.Add(c); changed = true; }
            }
        } while (changed);

        return removed.Except(gone, cmp).OrderBy(k => k, cmp).ToList();
    }

    static bool IsBepInEx(string key) => key.StartsWith("BepInEx-BepInExPack", StringComparison.OrdinalIgnoreCase);
}
