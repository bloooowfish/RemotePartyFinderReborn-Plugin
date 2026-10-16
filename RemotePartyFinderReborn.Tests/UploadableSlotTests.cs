using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RemotePartyFinderReborn.Tests;

public sealed class UploadableSlotTests {
    static UploadableSlotTests() => RuntimeHelpers.RunClassConstructor(typeof(UploadContractTests).TypeHandle);

    [Theory]
    [InlineData(0UL, 0L)]
    [InlineData(0x80000000UL, 2147483648L)]
    public void Uploadable_slot_keeps_legacy_numeric_wire_values(ulong rawFlags, long expected) {
        var payload = SerializeSlot(rawFlags);

        Assert.Equal(JTokenType.Integer, payload["accepting"]?.Type);
        Assert.Equal(expected, payload.Value<long>("accepting"));
    }

    [Fact]
    public void Uploadable_slot_preserves_beastmaster_flag_as_hex_without_uint_truncation() {
        const ulong beastmaster = 1UL << 32;
        var source = CreatePartyFinderSlot(beastmaster);

        // Dalamud's current public Accepting collection drops this flag because its indexer
        // truncates JobFlags to UInt32. Upload serialization must retain the stored raw mask.
        var accepting = source.GetType().GetProperty("Accepting")?.GetValue(source) as IEnumerable;
        Assert.NotNull(accepting);
        Assert.Empty(accepting.Cast<object>());

        var payload = SerializeSlot(source);
        Assert.Equal(JTokenType.String, payload["accepting"]?.Type);
        Assert.Equal("0x0000000100000000", payload.Value<string>("accepting"));
    }

    [Fact]
    public void Uploadable_slot_preserves_existing_bits_when_beastmaster_is_also_set() {
        const ulong rawFlags = (1UL << 32) | 0x80000000UL | 0x100UL;

        var payload = SerializeSlot(rawFlags);

        Assert.Equal("0x0000000180000100", payload.Value<string>("accepting"));
    }

    private static JObject SerializeSlot(ulong rawFlags) => SerializeSlot(CreatePartyFinderSlot(rawFlags));

    private static JObject SerializeSlot(object source) {
        var constructor = typeof(UploadableSlot).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
        return JObject.FromObject(constructor.Invoke([source]));
    }

    private static object CreatePartyFinderSlot(ulong rawFlags) {
        var slotType = typeof(UploadableSlot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0]
            .GetParameters()[0]
            .ParameterType;
        var constructor = slotType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(ulong)],
            modifiers: null
        );
        Assert.NotNull(constructor);
        return constructor.Invoke([rawFlags]);
    }
}
