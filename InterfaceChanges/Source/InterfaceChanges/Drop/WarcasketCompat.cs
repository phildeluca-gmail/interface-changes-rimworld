using RimWorld;

namespace InterfaceChanges.Drop
{
    // Recognises a Vanilla Factions Expanded - Pirates warcasket piece by the
    // runtime type name of its def, so VFEPirates.dll is never referenced.
    // Every warcasket piece is declared as <VFEPirates.WarcasketDef>; the
    // Thing's own class is not reliable (the bodysuit uses plain Apparel).
    // Same check, verified 2026-09-27, as Do Not Be Lazy's copy.
    public static class WarcasketCompat
    {
        private const string WarcasketDefTypeName = "VFEPirates.WarcasketDef";

        public static bool IsWarcasket(Apparel apparel)
        {
            return apparel?.def != null && apparel.def.GetType().FullName == WarcasketDefTypeName;
        }
    }
}
