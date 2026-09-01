#pragma warning disable IDE0130
#if !NET7_0_OR_GREATER
namespace System.Runtime.CompilerServices;

/// <summary>Specifies that a type has required members or that a member is required.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
[ComponentModel.EditorBrowsable(ComponentModel.EditorBrowsableState.Never)]
internal sealed class RequiredMemberAttribute : Attribute;
#endif
