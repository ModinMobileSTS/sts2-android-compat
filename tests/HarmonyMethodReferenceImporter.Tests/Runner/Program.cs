using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using MonoMod.Utils;
using MonoMod.Utils.Cil;
using SyntheticGame;
using SreOpCodes = System.Reflection.Emit.OpCodes;

internal static class Program
{
    private const string RepairHarmonyId = "com.sts2mobile.monomod.importer";
    private const string InitModifier = "System.Runtime.CompilerServices.IsExternalInit";
    private static int _moduleId;

    private static void Main()
    {
        var damageSetter = typeof(MegaCrit.Sts2.Core.Entities.Creatures.DamageResult)
            .GetProperty(nameof(MegaCrit.Sts2.Core.Entities.Creatures.DamageResult.UnblockedDamage))
            ?.SetMethod ?? throw new InvalidOperationException("DamageResult setter missing");

        Assert(!DirectImportHasInitModifier(damageSetter), "fixture must reproduce the packaged importer's missing modreq before repair");

        STS2Mobile.HarmonyMethodReferenceImporterShim.Initialize();
        AssertParameterModifierRepair();
        Assert(DirectImportHasInitModifier(damageSetter), "direct MMReflectionImporter path was not repaired");

        UnpatchDirectImporter();
        Assert(!DirectImportHasInitModifier(damageSetter), "direct importer postfix was not isolated for emitter fallback test");

        var classSetter = typeof(ClassCarrier).GetProperty(nameof(ClassCarrier.Value))?.SetMethod
            ?? throw new InvalidOperationException("ClassCarrier setter missing");
        var classReference = EmitSetterReference(typeof(ClassCarrier), classSetter, SreOpCodes.Callvirt, useEmitCall: false, receiverByReference: false);
        AssertInitModifier(classReference, "CecilILGenerator.Emit class callvirt");
        Assert(classReference.HasThis, "class setter lost instance calling convention");

        var structSetter = typeof(StructCarrier).GetProperty(nameof(StructCarrier.Value))?.SetMethod
            ?? throw new InvalidOperationException("StructCarrier setter missing");
        var structReference = EmitSetterReference(typeof(StructCarrier), structSetter, SreOpCodes.Call, useEmitCall: true, receiverByReference: true);
        AssertInitModifier(structReference, "CecilILGenerator.EmitCall struct call");
        Assert(structReference.HasThis, "struct setter lost instance calling convention");

        var genericSetter = typeof(GenericCarrier<int>).GetProperty(nameof(GenericCarrier<int>.Value))?.SetMethod
            ?? throw new InvalidOperationException("GenericCarrier setter missing");
        var genericReference = EmitSetterReference(typeof(GenericCarrier<int>), genericSetter, SreOpCodes.Callvirt, useEmitCall: true, receiverByReference: false);
        AssertInitModifier(genericReference, "CecilILGenerator.EmitCall closed generic callvirt");
        Assert(genericReference.Parameters[0].ParameterType is GenericParameter,
            $"closed generic setter parameter was flattened to {genericReference.Parameters[0].ParameterType.FullName}");

        AssertExecutableSetter(typeof(ClassCarrier), classSetter, "Value", 17);
        AssertExecutableSetter(typeof(GenericCarrier<int>), genericSetter, "Value", 29);

        Console.WriteLine("Harmony method-reference modifier regression passed.");
    }

    private static bool DirectImportHasInitModifier(MethodInfo setter)
    {
        using var module = CreateModule("direct");
        return HasInitModifier(module.ImportReference(setter));
    }


    private static void AssertParameterModifierRepair()
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("sts2mobile.parameter.modifier.fixture"),
            System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var moduleBuilder = assembly.DefineDynamicModule("fixture");
        var typeBuilder = moduleBuilder.DefineType(
            "Synthetic.ParameterCarrier",
            System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class);
        var methodBuilder = typeBuilder.DefineMethod(
            "Consume",
            System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static,
            CallingConventions.Standard,
            typeof(void),
            Type.EmptyTypes,
            Type.EmptyTypes,
            new[] { typeof(int) },
            new[] { new[] { typeof(System.Runtime.CompilerServices.IsExternalInit) } },
            new[] { Type.EmptyTypes });
        methodBuilder.GetILGenerator().Emit(SreOpCodes.Ret);
        var reflected = typeBuilder.CreateType()?.GetMethod("Consume")
            ?? throw new InvalidOperationException("Synthetic parameter modifier method missing");
        Assert(reflected.GetParameters()[0].GetRequiredCustomModifiers().Length == 1,
            "runtime did not expose the synthetic parameter modreq");

        using var module = CreateModule("parameter_modifier");
        var imported = module.ImportReference(reflected);
        Assert(ModifierNames(imported.Parameters[0].ParameterType).Count == 0,
            "packaged importer unexpectedly preserved the synthetic parameter modreq");

        var repair = typeof(STS2Mobile.HarmonyMethodReferenceImporterShim).GetMethod(
            "ApplyMethodReferenceModifiers",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(STS2Mobile.HarmonyMethodReferenceImporterShim).FullName, "ApplyMethodReferenceModifiers");
        Func<Type, object> importType = type => module.ImportReference(type, imported);
        var changed = (bool)repair.Invoke(null, new object[] { imported, reflected, importType });
        Assert(changed, "parameter custom modifier repair reported no change");
        Assert(ModifierNames(imported.Parameters[0].ParameterType).SetEquals(new[] { InitModifier }),
            $"parameter custom modifier repair produced {imported.Parameters[0].ParameterType.FullName}");
    }

    private static HashSet<string> ModifierNames(TypeReference typeReference)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var current = typeReference;
        while (current is TypeSpecification specification)
        {
            if (current is RequiredModifierType required)
                names.Add(required.ModifierType.FullName);
            else if (current is OptionalModifierType optional)
                names.Add(optional.ModifierType.FullName);
            current = specification.ElementType;
        }
        return names;
    }

    private static MethodReference EmitSetterReference(
        Type receiverType,
        MethodInfo setter,
        System.Reflection.Emit.OpCode opcode,
        bool useEmitCall,
        bool receiverByReference)
    {
        using var module = CreateModule("emit");
        var owner = AddOwner(module);
        var method = new MethodDefinition(
            "Invoke",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
            module.TypeSystem.Void);
        owner.Methods.Add(method);
        method.Parameters.Add(new ParameterDefinition(module.ImportReference(receiverType)));
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

        var generator = new CecilILGenerator(method.Body.GetILProcessor());
        if (receiverByReference)
            generator.Emit(SreOpCodes.Ldarga_S, (short)0);
        else
            generator.Emit(SreOpCodes.Ldarg_0);
        generator.Emit(SreOpCodes.Ldarg_1);
        if (useEmitCall)
            generator.EmitCall(opcode, setter, null);
        else
            generator.Emit(opcode, setter);
        generator.Emit(SreOpCodes.Ret);

        return method.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Single();
    }

    private static void AssertExecutableSetter(Type receiverType, MethodInfo setter, string propertyName, int expected)
    {
        using var module = CreateModule("execute");
        var owner = AddOwner(module);
        var method = new MethodDefinition(
            "Invoke",
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
            module.TypeSystem.Void);
        owner.Methods.Add(method);
        method.Parameters.Add(new ParameterDefinition(module.ImportReference(receiverType)));
        method.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));

        var generator = new CecilILGenerator(method.Body.GetILProcessor());
        generator.Emit(SreOpCodes.Ldarg_0);
        generator.Emit(SreOpCodes.Ldarg_1);
        generator.EmitCall(SreOpCodes.Callvirt, setter, null);
        generator.Emit(SreOpCodes.Ret);

        using var stream = new MemoryStream();
        module.Write(stream);
        var assembly = Assembly.Load(stream.ToArray());
        var instance = Activator.CreateInstance(receiverType)
            ?? throw new InvalidOperationException($"Could not create {receiverType}");
        assembly.GetType("Synthetic.Invoker")?.GetMethod("Invoke")?.Invoke(null, new object[] { instance, expected });
        var actual = receiverType.GetProperty(propertyName)?.GetValue(instance);
        Assert(Equals(actual, expected), $"emitted {receiverType.Name} setter returned {actual}, expected {expected}");
    }

    private static ModuleDefinition CreateModule(string purpose)
    {
        var name = $"sts2mobile_modifier_{purpose}_{++_moduleId}";
        return ModuleDefinition.CreateModule(name, new ModuleParameters
        {
            Kind = ModuleKind.Dll,
            ReflectionImporterProvider = MMReflectionImporter.ProviderNoDefault,
        });
    }

    private static TypeDefinition AddOwner(ModuleDefinition module)
    {
        var owner = new TypeDefinition(
            "Synthetic",
            "Invoker",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            module.TypeSystem.Object);
        module.Types.Add(owner);
        return owner;
    }

    private static void UnpatchDirectImporter()
    {
        var target = typeof(MMReflectionImporter).GetMethod(
            "ImportReference",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(MethodBase), typeof(IGenericParameterProvider) },
            null) ?? throw new MissingMethodException(typeof(MMReflectionImporter).FullName, "ImportReference");
        new Harmony("sts2mobile.modifier.tests").Unpatch(target, HarmonyPatchType.Postfix, RepairHarmonyId);
    }

    private static void AssertInitModifier(MethodReference reference, string path)
    {
        Assert(HasInitModifier(reference), $"{path} did not preserve modreq({InitModifier}): {reference.FullName}");
    }

    private static bool HasInitModifier(MethodReference reference)
    {
        return reference.ReturnType is RequiredModifierType modifier
            && string.Equals(modifier.ModifierType.FullName, InitModifier, StringComparison.Ordinal)
            && string.Equals(modifier.ElementType.FullName, "System.Void", StringComparison.Ordinal);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

namespace STS2Mobile
{
    public static class PatchHelper
    {
        public static readonly List<string> Messages = new();

        public static void Log(string message)
        {
            Messages.Add(message);
            Console.WriteLine(message);
        }
    }
}
