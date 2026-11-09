using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Ryan6Vrc.AvatarTools.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.SDK3.Avatars.ScriptableObjects;

// vrc.bits / vrc.signed / vrc.lerp: the schema surface, the validation refusals, and the emitted expression parameters.
public class ParamEncodingTests
{
    private const string TestRoot = "Assets/Agent/Scratch/paramenc_tests";
    private readonly AnimatorTestHelpers.UnownedSideAssetSweep _sweep = new AnimatorTestHelpers.UnownedSideAssetSweep();

    [SetUp]
    public void SetUp() => _sweep.Begin();

    [TearDown]
    public void TearDown()
    {
        _sweep.End();
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        if (Directory.Exists(TestRoot)) Directory.Delete(TestRoot, true);
    }

    private static string Doc(string paramsBlock, string controller = "Enc") =>
        "schema: 1\ncontroller: " + controller + "\nbasis: avatar-root\nparameters:\n" + paramsBlock;

    private static System.Collections.Generic.List<string> Validate(string paramsBlock) =>
        SchemaValidation.Validate(AnimatorSchemaYaml.Parse(Doc(paramsBlock), "mem://enc"));

    // The motivating case: a 2-bit unsigned float with a 0.2 s custom lerp, synced and written over OSC.
    private const string DialParam = "  Dial: { type: float, vrc: { synced: true, osc: true, bits: 2, signed: false, lerp: 0.2 } }\n";

    [Test]
    public void Parse_And_Serialize_RoundTrip_The_Three_Fields()
    {
        var v = AnimatorSchemaYaml.Parse(Doc(DialParam), "mem://enc").Parameters[0].Vrc;
        Assert.AreEqual(2, v.Bits);
        Assert.AreEqual(false, v.Signed);
        Assert.AreEqual(0.2f, v.Lerp.Value, 1e-6f);

        string text = AnimatorSchemaEmit.Serialize(AnimatorSchemaYaml.Parse(Doc(DialParam), "mem://enc"));
        var back = AnimatorSchemaYaml.Parse(text, "roundtrip").Parameters[0].Vrc;
        Assert.AreEqual(2, back.Bits, text);
        Assert.AreEqual(false, back.Signed, text);
        Assert.AreEqual(0.2f, back.Lerp.Value, 1e-6f, text);
    }

    [Test]
    public void Parse_Refuses_A_Fractional_Bits()
    {
        var ex = Assert.Throws<SchemaException>(() => AnimatorSchemaYaml.Parse(
            Doc("  Dial: { type: float, vrc: { synced: true, bits: 2.5 } }\n"), "mem://enc"));
        StringAssert.Contains("'Dial' vrc.bits", ex.Message);
    }

    // The validation bounds mirror SDK constants because SchemaValidation is SDK-free; this pins the mirror.
    [Test]
    public void Validation_Bounds_Match_The_Installed_SDK()
    {
#if VRC_PARAM_ENCODING_PRESENT
        Assert.AreEqual((int)VRCExpressionParameters.Parameter.MinBitWidth, SchemaValidation.MinBits);
        Assert.AreEqual((int)VRCExpressionParameters.Parameter.MaxBitWidth, SchemaValidation.MaxBits);
        Assert.AreEqual(0f, VRCExpressionParameters.Parameter.MinLerpDuration);
        Assert.AreEqual(VRCExpressionParameters.Parameter.MaxLerpDuration, SchemaValidation.MaxLerpSeconds);
#else
        Assert.Ignore("this SDK has no bitWidth/lerp fields (com.vrchat.avatars < 3.10.6-beta.4)");
#endif
    }

    [Test]
    public void Validate_Accepts_The_Bounds()
    {
        Assert.IsEmpty(Validate(
            DialParam +
            "  A: { type: int, vrc: { synced: true, bits: 2, lerp: 0 } }\n" +
            "  B: { type: float, vrc: { synced: true, bits: 8, signed: true, lerp: 300 } }\n" +
            "  W: { type: bool, vrc: { synced: true, type: int, bits: 3 } }\n"));
    }

    [TestCase("  P: { type: float, vrc: { synced: true, bits: 1 } }\n", "param-bits")]
    [TestCase("  P: { type: int, vrc: { synced: true, bits: 9 } }\n", "param-bits")]
    [TestCase("  P: { type: float, vrc: { synced: true, lerp: -0.1 } }\n", "param-lerp")]
    [TestCase("  P: { type: float, vrc: { synced: true, lerp: 301 } }\n", "param-lerp")]
    [TestCase("  P: { type: bool, vrc: { synced: true, bits: 2 } }\n", "param-encoding")]
    [TestCase("  P: { type: bool, vrc: { synced: true, signed: true } }\n", "param-encoding")]
    [TestCase("  P: { type: bool, vrc: { synced: true, lerp: 1 } }\n", "param-encoding")]
    [TestCase("  P: { type: float, vrc: { synced: true, type: bool, bits: 2 } }\n", "param-encoding")]
    [TestCase("  P: { type: float, vrc: { bits: 2 } }\n", "param-encoding")]
    [TestCase("  P: { type: float, vrc: { synced: false, lerp: 1 } }\n", "param-encoding")]
    [TestCase("  P: { type: float, scratch: true, vrc: { synced: true, bits: 2 } }\n", "param-encoding")]
    public void Validate_Refuses(string paramsBlock, string rule)
    {
        var errors = Validate(paramsBlock);
        Assert.IsTrue(errors.Any(e => e.StartsWith("# " + rule + ":") && e.Contains("'P'")), string.Join("\n", errors));
    }

#if VRC_PARAM_ENCODING_PRESENT
    private static VRCExpressionParameters.Parameter[] Emit(string paramsBlock)
    {
        ControllerEmit.Build(AnimatorSchemaYaml.Parse(Doc(paramsBlock), "mem://enc"), out var r);
        return r.Params.parameters;
    }

    [Test]
    public void Emit_TwoBit_Unsigned_Float_With_Lerp()
    {
        var p = Emit(DialParam).Single();
        Assert.AreEqual(VRCExpressionParameters.ValueType.Float, p.valueType);
        Assert.IsTrue(p.networkSynced);
        Assert.AreEqual(2, p.bitWidth);
        Assert.IsTrue(p.flippedRepresentation, "a float is signed by default, so unsigned is the flipped form");
        Assert.IsTrue(p.useCustomLerpDuration);
        Assert.AreEqual(0.2f, p.customLerpDuration, 1e-6f);
    }

    [Test]
    public void Representation_Maps_Per_Wire_Type()
    {
        var ps = Emit(
            "  FSigned: { type: float, vrc: { synced: true, signed: true } }\n" +
            "  FUnsigned: { type: float, vrc: { synced: true, signed: false } }\n" +
            "  ISigned: { type: int, vrc: { synced: true, signed: true } }\n" +
            "  IUnsigned: { type: int, vrc: { synced: true, signed: false } }\n" +
            "  Wire: { type: float, vrc: { synced: true, type: int, signed: true } }\n");
        bool Flipped(string n) => ps.First(x => x.name == n).flippedRepresentation;
        Assert.IsFalse(Flipped("FSigned"));
        Assert.IsTrue(Flipped("FUnsigned"));
        Assert.IsTrue(Flipped("ISigned"));
        Assert.IsFalse(Flipped("IUnsigned"));
        Assert.IsTrue(Flipped("Wire"), "the wire type (int), not the animator type, decides the default");
    }

    [Test]
    public void Lerp_Zero_Is_A_Declared_Custom_Lerp()
    {
        var p = Emit("  Mode: { type: int, vrc: { synced: true, lerp: 0 } }\n").Single();
        Assert.IsTrue(p.useCustomLerpDuration);
        Assert.AreEqual(0f, p.customLerpDuration);
    }

    [Test]
    public void Omitted_Fields_Emit_What_A_Plain_Parameter_Carries()
    {
        var emitted = Emit(
            "  F: { type: float, default: 0.5, vrc: { synced: true, saved: true } }\n" +
            "  I: { type: int, vrc: { synced: true } }\n" +
            "  B: { type: bool, vrc: { synced: true } }\n" +
            "  U: float\n");
        var plain = new VRCExpressionParameters.Parameter();
        foreach (var p in emitted)
        {
            Assert.AreEqual(plain.bitWidth, p.bitWidth, p.name + " bitWidth");
            Assert.AreEqual(plain.flippedRepresentation, p.flippedRepresentation, p.name + " flippedRepresentation");
            Assert.AreEqual(plain.useCustomLerpDuration, p.useCustomLerpDuration, p.name + " useCustomLerpDuration");
            Assert.AreEqual(plain.customLerpDuration, p.customLerpDuration, p.name + " customLerpDuration");
        }
    }

    [Test]
    public void Emit_Refuses_The_Fields_On_A_VRC_BuiltIn()
    {
        var ex = Assert.Throws<ControllerEmit.EmitException>(() => Emit(
            "  GestureLeft: { type: int, vrc: { synced: true, bits: 3 } }\n"));
        StringAssert.Contains("'GestureLeft'", ex.Message);
    }

    [Test]
    public void ParamsDiff_Names_A_Changed_Encoding()
    {
        VRCExpressionParameters Asset(int bits, bool flipped, float lerp)
        {
            var a = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            a.parameters = new[] { new VRCExpressionParameters.Parameter {
                name = "P", valueType = VRCExpressionParameters.ValueType.Float, networkSynced = true,
                bitWidth = (byte)bits, flippedRepresentation = flipped, useCustomLerpDuration = true, customLerpDuration = lerp } };
            return a;
        }
        Assert.IsNull(ControllerFixpoint.ParamsDiff(Asset(4, true, 1), Asset(4, true, 1), "params"));
        StringAssert.Contains("bitWidth 4 vs 2", ControllerFixpoint.ParamsDiff(Asset(4, true, 1), Asset(2, true, 1), "params"));
        StringAssert.Contains("flippedRepresentation", ControllerFixpoint.ParamsDiff(Asset(4, true, 1), Asset(4, false, 1), "params"));
        StringAssert.Contains("customLerpDuration", ControllerFixpoint.ParamsDiff(Asset(4, true, 1), Asset(4, true, 2), "params"));
    }

    [Test]
    public void CompileController_Writes_The_Fields_Into_The_Params_Asset()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        string src = TestRoot + "/Good.yaml";
        File.WriteAllText(src, Doc(DialParam, "EncGood"));
        string result = CompileController.Run(src, TestRoot + "/out", whatIf: false);
        StringAssert.Contains("=> OK", result);

        var asset = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(TestRoot + "/out/EncGood_Parameters.asset");
        Assert.IsNotNull(asset, result);
        var p = asset.parameters.Single();
        Assert.AreEqual(2, p.bitWidth);
        Assert.IsTrue(p.flippedRepresentation);
        Assert.IsTrue(p.networkSynced);
        Assert.IsTrue(p.useCustomLerpDuration);
        Assert.AreEqual(0.2f, p.customLerpDuration, 1e-6f);
    }
#else
    [Test]
    public void CompileController_Refuses_The_Fields_On_An_SDK_Without_Them()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        string src = TestRoot + "/Old.yaml";
        File.WriteAllText(src, Doc(DialParam, "EncOld"));
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*emit:.*'Dial'.*3\.10\.6-beta\.4.*=> FAIL"));
        StringAssert.Contains("FAIL", CompileController.Run(src, TestRoot + "/out", whatIf: false));
    }
#endif

    [Test]
    public void CompileController_Names_A_Refusal()
    {
        AnimatorTestHelpers.EnsureFolder(TestRoot);
        string src = TestRoot + "/Bad.yaml";
        File.WriteAllText(src, Doc("  Flag: { type: bool, vrc: { synced: true, bits: 2 } }\n", "EncBad"));
        LogAssert.Expect(LogType.Error, new Regex(@"\[CompileController\] .*validation failed.*param-encoding.*'Flag'.*=> FAIL"));
        string result = CompileController.Run(src, TestRoot + "/outbad", whatIf: false);
        StringAssert.Contains("FAIL", result);
        Assert.IsFalse(File.Exists(TestRoot + "/outbad/EncBad_Parameters.asset"));
        int i = result.IndexOf("log=", System.StringComparison.Ordinal);
        if (i >= 0) AssetDatabase.DeleteAsset(result.Substring(i + 4).Trim());
    }
}
