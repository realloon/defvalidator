namespace Verse;

public class Def
{
    public string? defName;
    public string? label;
}

public class ThingDef : Def
{
    public int statBase;
    public SoundDef? sound;
    public List<ThingDef>? thingRefs;
    public List<CompProperties>? comps;
    public List<DefModExtension>? modExtensions;
}

public class SoundDef : Def
{
}

public class ColorDef : Def
{
    public string? rgb;
}

public class DefModExtension
{
    public string? tag;
}

public class CompProperties
{
    public Type? compClass;
}

public class CompProperties_Glower : CompProperties
{
    public int glowRadius;
}

