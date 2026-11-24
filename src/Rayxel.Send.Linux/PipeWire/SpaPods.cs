using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.PipeWire;

/// <summary>
/// The few SPA pods the capture builds, written byte for byte as spa/pod/builder.h writes them.
/// SPA's builder lives entirely in inline functions and macros, so no library has it to call.
/// <para>
/// The rules it follows, all of which are reproduced here: every pod is a header of two words -
/// body size, type - and a body, padded with zeros to a multiple of eight bytes; an object's body
/// is its type and id and then its properties, each a key, a flags word and a pod; and a choice
/// writes its first value as a whole pod, whose header then serves as the header of every value,
/// the rest following as bare bodies without padding between them. The padding after a choice,
/// like that after any pod, counts towards the object around it. Values are in the host's byte
/// order, as SPA's own are.
/// </para>
/// </summary>
internal ref struct PodBuilder
{
    private readonly Span<byte> _buffer;
    private int _offset;

    /// <param name="buffer">Eight-byte aligned, as a pod must be for the parser on the other side to take it.</param>
    public PodBuilder(Span<byte> buffer)
    {
        buffer.Clear();
        _buffer = buffer;
        _offset = 0;
    }

    /// <summary>Where the object starts, for <see cref="EndObject"/>.</summary>
    public int BeginObject(uint type, uint id)
    {
        int start = _offset;
        Pod(0, Spa.TypeObject);
        Word(type);
        Word(id);
        return start;
    }

    /// <summary>Fills in the object's size: its body, from its type onwards, to here.</summary>
    public void EndObject(int start) =>
        MemoryMarshal.Write(_buffer[start..], (uint)(_offset - start - 8));

    public void Id(uint key, uint value)
    {
        Key(key);
        Pod(4, Spa.TypeId);
        Word(value);
        Pad();
    }

    public void Int(uint key, int value)
    {
        Key(key);
        Pod(4, Spa.TypeInt);
        Word((uint)value);
        Pad();
    }

    public void Rectangle(uint key, uint width, uint height)
    {
        Key(key);
        Pod(8, Spa.TypeRectangle);
        Word(width);
        Word(height);
    }

    /// <summary>SPA_POD_CHOICE_RANGE_Rectangle: the default, then the least and the most.</summary>
    public void RectangleRange(uint key, uint width, uint height, uint minWidth, uint minHeight, uint maxWidth,
                               uint maxHeight) =>
        Choice(key, Spa.ChoiceRange, 8, Spa.TypeRectangle, [width, height, minWidth, minHeight, maxWidth, maxHeight]);

    /// <summary>SPA_POD_CHOICE_RANGE_Fraction: the default, then the least and the most.</summary>
    public void FractionRange(uint key, uint num, uint denom, uint minNum, uint minDenom, uint maxNum, uint maxDenom) =>
        Choice(key, Spa.ChoiceRange, 8, Spa.TypeFraction, [num, denom, minNum, minDenom, maxNum, maxDenom]);

    /// <summary>SPA_POD_CHOICE_FLAGS_Int: one value, the flags that may be set.</summary>
    public void IntFlags(uint key, int flags) =>
        Choice(key, Spa.ChoiceFlags, 4, Spa.TypeInt, [(uint)flags]);

    private void Choice(uint key, uint choice, uint childSize, uint childType, scoped ReadOnlySpan<uint> values)
    {
        Key(key);
        Pod(16 + (uint)values.Length * 4, Spa.TypeChoice);
        Word(choice);
        Word(0);
        Pod(childSize, childType);
        foreach (uint value in values) Word(value);
        Pad();
    }

    private void Key(uint key)
    {
        Word(key);
        Word(0); // flags
    }

    private void Pod(uint size, uint type)
    {
        Word(size);
        Word(type);
    }

    private void Word(uint value)
    {
        if (_offset + 4 > _buffer.Length) throw new InvalidOperationException("a pod outgrew the space given for it");
        MemoryMarshal.Write(_buffer[_offset..], in value);
        _offset += 4;
    }

    private void Pad()
    {
        while ((_offset & 7) != 0) Word(0);
    }
}

/// <summary>
/// Reading a pod PipeWire hands over, the way spa_pod_parse_object reads one: properties found
/// by key, and a property that is a choice taken only when it is of the kind "none", for its
/// first value - which is how a property that was negotiated arrives even after fixation. A
/// property that is missing, of another type, or still a real choice counts as not there.
/// </summary>
internal static unsafe class PodReader
{
    /// <summary>spa_pod_is_object, and of this object type.</summary>
    public static bool IsObject(SpaPod* pod, uint objectType) =>
        pod != null && pod->Type == Spa.TypeObject && pod->Size >= 8 && *(uint*)(pod + 1) == objectType;

    /// <summary>The value of the object's property <paramref name="key"/>, or null; spa_pod_find_prop.</summary>
    public static SpaPod* Find(SpaPod* obj, uint key)
    {
        if (obj == null || obj->Type != Spa.TypeObject || obj->Size < 8) return null;
        byte* end = (byte*)(obj + 1) + obj->Size;

        // Each property is its key, its flags and its pod; the next one starts eight-aligned.
        for (byte* prop = (byte*)(obj + 1) + 8; prop + 16 <= end;)
        {
            var value = (SpaPod*)(prop + 8);
            ulong size = 16ul + value->Size;
            if ((ulong)(end - prop) < size) break;
            if (*(uint*)prop == key) return value;
            prop += (size + 7) & ~7ul;
        }
        return null;
    }

    public static bool Has(SpaPod* obj, uint key) => Find(obj, key) != null;

    public static bool Id(SpaPod* obj, uint key, out uint value)
    {
        SpaPod* pod = Collectable(Find(obj, key), Spa.TypeId, 4);
        value = pod != null ? *(uint*)(pod + 1) : 0;
        return pod != null;
    }

    public static bool Rectangle(SpaPod* obj, uint key, out uint width, out uint height) =>
        Words(Collectable(Find(obj, key), Spa.TypeRectangle, 8), out width, out height);

    /// <summary>A fraction is laid out as a rectangle is: two words, numerator first.</summary>
    public static bool Fraction(SpaPod* obj, uint key, out uint num, out uint denom) =>
        Words(Collectable(Find(obj, key), Spa.TypeFraction, 8), out num, out denom);

    private static bool Words(SpaPod* pod, out uint first, out uint second)
    {
        first = pod != null ? ((uint*)(pod + 1))[0] : 0;
        second = pod != null ? ((uint*)(pod + 1))[1] : 0;
        return pod != null;
    }

    /// <summary>spa_pod_parser_can_collect followed by the unwrapping of a "none" choice.</summary>
    private static SpaPod* Collectable(SpaPod* value, uint type, uint bodySize)
    {
        if (value == null) return null;
        if (value->Type == Spa.TypeChoice)
        {
            // spa_pod_choice_body: the choice's kind, its flags, then the child's header.
            if (value->Size < 16) return null;
            if (*(uint*)(value + 1) != Spa.ChoiceNone) return null;
            value = (SpaPod*)((byte*)value + 16);
        }
        return value->Type == type && value->Size >= bodySize ? value : null;
    }
}
