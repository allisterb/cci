﻿//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.IO;
//using Microsoft.Cci;
using Microsoft.Cci.MutableCodeModel;
using System.Collections.Generic;
using Microsoft.Cci.Contracts;

namespace Microsoft.Cci.ILToCodeModel {

  /// <summary>
  /// A contract provider that can be used to get contracts from a unit by querying in
  /// a random-access manner. That is, the unit is *not* traversed eagerly.
  /// </summary>
  public class LazyContractProvider : IContractProvider {

    /// <summary>
    /// Needed because the decompiler requires the concrete class ContractProvider
    /// </summary>
    ContractProvider underlyingContractProvider;
    /// <summary>
    /// needed to pass to decompiler
    /// </summary>
    IMetadataHost host;
    /// <summary>
    /// needed to pass to decompiler
    /// </summary>
    private PdbReader pdbReader;

    private IUnit unit; // the module this is a lazy provider for

    /// <summary>
    /// Allocates an object that can be used to query for contracts by asking questions about specific methods/types, etc.
    /// </summary>
    /// <param name="host">The host that loaded the unit for which this is to be a contract provider.</param>
    /// <param name="unit">The unit to retrieve the contracts from.</param>
    /// <param name="contractMethods">A collection of methods that can be called in a way that provides tools with information about contracts.</param>
    public LazyContractProvider(IMetadataHost host, IUnit unit, IContractMethods contractMethods) {
      this.host = host;
      this.underlyingContractProvider = new ContractProvider(contractMethods, unit);
      string pdbFile = Path.ChangeExtension(unit.Location, "pdb");
      if (File.Exists(pdbFile)) {
        Stream pdbStream = File.OpenRead(pdbFile);
        this.pdbReader = new PdbReader(pdbStream, host);
      }
      this.unit = unit;
    }

    /// <summary>
    /// The unit that this is a contract provider for. Intentional design:
    /// no provider works on more than one unit.
    /// </summary>
    /// <value></value>
    public IUnit Unit { get { return this.unit; } }

    /// <summary>
    /// Gets the host.
    /// </summary>
    /// <value>The host.</value>
    public IMetadataHost Host { get { return this.host; } }

    #region IContractProvider Members

    /// <summary>
    /// Returns the loop contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="loop">An object that might have been associated with a loop contract. This can be any kind of object.</param>
    /// <returns></returns>
    public ILoopContract/*?*/ GetLoopContractFor(object loop) {
      throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the method contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="method">An object that might have been associated with a method contract. This can be any kind of object.</param>
    /// <returns></returns>
    public IMethodContract/*?*/ GetMethodContractFor(object method) {

      IMethodContract contract = this.underlyingContractProvider.GetMethodContractFor(method);
      if (contract != null) return contract == ContractDummy.MethodContract ? null : contract;

      IMethodReference methodReference = method as IMethodReference;
      if (methodReference == null) {
        this.underlyingContractProvider.AssociateMethodWithContract(method, ContractDummy.MethodContract);
        return null;
      }

      IMethodDefinition methodDefinition = methodReference.ResolvedMethod;
      if (methodDefinition == Dummy.Method) {
        this.underlyingContractProvider.AssociateMethodWithContract(method, ContractDummy.MethodContract);
        return null;
      }

      #region Interface methods don't contain contracts directly, but point to a class holding their contracts
      if (methodDefinition.ContainingTypeDefinition.IsInterface) {
        IMethodDefinition/*?*/ contractClassMethod = ContractHelper.GetMethodFromContractClass(methodDefinition);
        if (contractClassMethod != null) {
          methodDefinition = contractClassMethod;
        }
      }
      #endregion Interface methods don't contain contracts directly, but point to a class holding their contracts

      if (methodDefinition.IsAbstract || methodDefinition.IsExternal) { // precondition of Body getter
        this.underlyingContractProvider.AssociateMethodWithContract(method, ContractDummy.MethodContract);
        return null;
      }

      IMethodBody methodBody = methodDefinition.Body;
      ISourceMethodBody/*?*/ sourceMethodBody = methodBody as ISourceMethodBody;
      if (sourceMethodBody == null) {
        sourceMethodBody = new SourceMethodBody(methodBody, this.host, this.underlyingContractProvider, this.pdbReader, true);
      }
      var dummyJustToGetDecompilationAndContractExtraction = sourceMethodBody.Block;

      // Now ask for the contract
      var methodContract = this.underlyingContractProvider.GetMethodContractFor(methodDefinition);
      if (methodContract == null) {
        this.underlyingContractProvider.AssociateMethodWithContract(method, ContractDummy.MethodContract); // so we don't try to extract more than once
      }
      return methodContract;
    }

    /// <summary>
    /// Returns the triggers, if any, that have been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="quantifier">An object that might have been associated with triggers. This can be any kind of object.</param>
    /// <returns></returns>
    public IEnumerable<IEnumerable<IExpression>>/*?*/ GetTriggersFor(object quantifier) {
      throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the type contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="type">An object that might have been associated with a type contract. This can be any kind of object.</param>
    /// <returns></returns>
    public ITypeContract/*?*/ GetTypeContractFor(object type) {

      ITypeContract/*?*/ typeContract = this.underlyingContractProvider.GetTypeContractFor(type);
      if (typeContract != null) return typeContract == ContractDummy.TypeContract ? null : typeContract;

      ITypeReference/*?*/ typeReference = type as ITypeReference;
      if (typeReference == null) {
        this.underlyingContractProvider.AssociateTypeWithContract(type, ContractDummy.TypeContract);
        return null;
      }

      ITypeDefinition/*?*/ typeDefinition = typeReference.ResolvedType;
      if (typeDefinition == null) {
        this.underlyingContractProvider.AssociateTypeWithContract(type, ContractDummy.TypeContract);
        return null;
      }

      IMethodDefinition/*?*/ invariantMethod = ContractHelper.GetInvariantMethod(typeDefinition);
      if (invariantMethod == null) {
        this.underlyingContractProvider.AssociateTypeWithContract(type, ContractDummy.TypeContract);
        return null;
      }
      IMethodBody methodBody = invariantMethod.Body;
      ISourceMethodBody/*?*/ srcMethodBody = methodBody as ISourceMethodBody;
      Microsoft.Cci.ILToCodeModel.SourceMethodBody sourceMethodBody;
      if (srcMethodBody == null)
        sourceMethodBody = new Microsoft.Cci.ILToCodeModel.SourceMethodBody(methodBody, this.host, null, null);
      else
        sourceMethodBody = srcMethodBody as Microsoft.Cci.ILToCodeModel.SourceMethodBody; // Remove all this once the code is moved into CCI2 and can access platformType
      BaseCodeTraverser bct = new BaseCodeTraverser();
      bct.Visit(sourceMethodBody.Block);
      // If it was properly decompiled, then it should just contain a sequence of calls to Contract.Invariant
      // although there might be empty statements and then a final return statement
      // REVIEW: Should this be done with a visitor?
      TypeContract tc = new TypeContract();
      foreach (var s in sourceMethodBody.Block.Statements) {
        IExpressionStatement/*?*/ expressionStatement = s as IExpressionStatement;
        if (expressionStatement == null) continue;
        IMethodCall/*?*/ methodCall = expressionStatement.Expression as IMethodCall;
        if (methodCall == null) continue;
        IMethodReference methodToCall = methodCall.MethodToCall;
        // Use this commented out line when the code is moved into CCI2
        //if (TypeHelper.TypesAreEquivalent(methodToCall.ContainingType, sourceMethodBody.platformType.SystemDiagnosticsContractsCodeContract)) {
        INamespaceTypeReference ntr = methodToCall.ContainingType as INamespaceTypeReference;
        if (ntr != null && ntr.Name.Value == "Contract") {
          string mname = methodToCall.Name.Value;
          List<IExpression> arguments = new List<IExpression>(methodCall.Arguments);
          int numArgs = arguments.Count;
          if (numArgs == 1 && mname == "Invariant") {
            List<ILocation> locations = new List<ILocation>(methodCall.Locations);
            TypeInvariant ti = new TypeInvariant();
            ti.Condition = arguments[0];
            ti.IsAxiom = false;
            ti.Locations = locations;
            tc.Invariants.Add(ti);
          }
        }
      }
      if (0 < tc.Invariants.Count) {
        this.underlyingContractProvider.AssociateTypeWithContract(typeReference, tc);
        return tc;
      } else {
        this.underlyingContractProvider.AssociateTypeWithContract(typeReference, ContractDummy.TypeContract);
        return null;
      }


    }

    /// <summary>
    /// A collection of methods that can be called in a way that provides tools with information about contracts.
    /// </summary>
    /// <value></value>
    public IContractMethods ContractMethods {
      get { return this.underlyingContractProvider.ContractMethods; }
    }

    #endregion
  }

  /// <summary>
  /// Not used for now.
  /// </summary>
  internal class MethodMapper : CodeAndContractMutator {
    IMethodDefinition targetMethod;
    IMethodDefinition sourceMethod;
    public MethodMapper(IMetadataHost host, IMethodDefinition targetMethod, IMethodDefinition sourceMethod)
      : base(host, true) {
      this.targetMethod = targetMethod;
      this.sourceMethod = sourceMethod;
    }
    public override object GetMutableCopyIfItExists(IParameterDefinition parameterDefinition) {
      if (parameterDefinition.ContainingSignature == sourceMethod) {
        var ps = new List<IParameterDefinition>(targetMethod.Parameters);
        return ps[parameterDefinition.Index];
      }
      return base.GetMutableCopyIfItExists(parameterDefinition);
    }
  }

  /// <summary>
  /// A mutator that changes all references defined in one unit into being
  /// references defined in another unit.
  /// It does so by substituting the target unit's identity for the source
  /// unit's identity whenever it visits a unit reference.
  /// Other than that, it overrides all visit methods that visit things which could be either
  /// a reference or a definition. The base class does *not* visit definitions
  /// when they are being seen as references because it assumes that the definitions
  /// will get visited during a top-down visit of the unit. But this visitor can
  /// be used on just small snippets of trees. A side effect is that all definitions
  /// are replaced by references so it doesn't preserve that aspect of the object model.
  /// </summary>
  internal class MappingMutator : CodeAndContractMutator {

    private IUnit sourceUnit = null;
    private UnitIdentity sourceUnitIdentity;
    private IUnit targetUnit = null;

    /// <summary>
    /// A mutator that, when it visits anything, converts any references defined in the <paramref name="sourceUnit"/>
    /// into references defined in the <paramref name="targetUnit"/>
    /// </summary>
    /// <param name="host">
    /// The host that loaded the <paramref name="targetUnit"/>
    /// </param>
    /// <param name="targetUnit">
    /// The unit to which all references in the <paramref name="sourceUnit"/>
    /// will mapped.
    /// </param>
    /// <param name="sourceUnit">
    /// The unit from which references will be mapped into references from the <paramref name="targetUnit"/>
    /// </param>
    public MappingMutator(IMetadataHost host, IUnit targetUnit, IUnit sourceUnit)
      : base(host, false) { // NB!! Must make this mutator *always* copy (i.e., pass false to the base ctor) or it screws up ASTs that shouldn't be changed
      this.sourceUnit = sourceUnit;
      this.sourceUnitIdentity = sourceUnit.UnitIdentity;
      this.targetUnit = targetUnit;
    }

    #region Units
    public override IUnitReference Visit(IUnitReference unitReference) {
      if (unitReference.UnitIdentity.Equals(this.sourceUnitIdentity))
        return targetUnit;
      else
        return base.Visit(unitReference);
    }
    #endregion Units

    #region Namespaces
    public override IRootUnitNamespaceReference Visit(IRootUnitNamespaceReference rootUnitNamespaceReference) {
      return this.Visit(this.GetMutableCopy(rootUnitNamespaceReference));
    }
    public override INestedUnitNamespaceReference Visit(INestedUnitNamespaceReference nestedUnitNamespaceReference) {
      return this.Visit(this.GetMutableCopy(nestedUnitNamespaceReference));
    }
    #endregion Namespaces

    #region Types
    public override INamespaceTypeReference Visit(INamespaceTypeReference namespaceTypeReference) {
      return this.Visit(this.GetMutableCopy(namespaceTypeReference));
    }
    public override INestedTypeReference Visit(INestedTypeReference nestedTypeReference) {
      return this.Visit(this.GetMutableCopy(nestedTypeReference));
    }
    public override IGenericTypeParameterReference Visit(IGenericTypeParameterReference genericTypeParameterReference) {
      return this.Visit(this.GetMutableCopy(genericTypeParameterReference));
    }
    #endregion Types

    #region Methods
    public override IMethodReference Visit(IMethodReference methodReference) {
      return this.Visit(this.GetMutableCopy(methodReference));
    }
    #endregion Methods

    #region Fields
    public override IFieldReference Visit(IFieldReference fieldReference) {
      return this.Visit(this.GetMutableCopy(fieldReference));
    }
    #endregion Fields

  }

  /// <summary>
  /// A contract provider that serves up the union of the contracts found from a set of contract providers.
  /// One provider is the primary provider: all contracts retrieved from this contract provider are expressed
  /// in terms of the types/members as defined by that provider. Optionally, a set of secondary providers
  /// are used to query for contracts on equivalent methods/types: any contracts found are transformed into
  /// being contracts expressed over the types/members as defined by the primary provider and additively
  /// merged into the contracts from the primary provider.
  /// </summary>
  public class AggregatingContractProvider : IContractProvider {

    private IUnit unit;
    private IContractProvider primaryProvider;
    private List<IContractProvider> oobProviders;
    ContractProvider underlyingContractProvider; // used just because it provides a store so this provider can cache its results
    IMetadataHost host;
    private Dictionary<IContractProvider, MappingMutator> mapperForOobToPrimary = new Dictionary<IContractProvider, MappingMutator>();
    private Dictionary<IContractProvider, MappingMutator> mapperForPrimaryToOob = new Dictionary<IContractProvider, MappingMutator>();

    /// <summary>
    /// The constructor for creating an aggregating provider.
    /// </summary>
    /// <param name="host">This is the host that loaded the unit for which the <paramref name="primaryProvider"/> is
    /// the provider for.
    /// </param>
    /// <param name="primaryProvider">
    /// The provider that will be used to define the types/members of things referred to in contracts.
    /// </param>
    /// <param name="oobProvidersAndHosts">
    /// These are optional. If non-null, then it must be a finite sequence of pairs: each pair is a contract provider
    /// and the host that loaded the unit for which it is a provider.
    /// </param>
    public AggregatingContractProvider(IMetadataHost host, IContractProvider primaryProvider, IEnumerable<KeyValuePair<IContractProvider,IMetadataHost>>/*?*/ oobProvidersAndHosts) {
      var primaryUnit = primaryProvider.Unit;
      this.unit = primaryUnit;
      this.primaryProvider = primaryProvider;

      this.underlyingContractProvider = new ContractProvider(primaryProvider.ContractMethods, primaryUnit);
      this.host = host;

      if (oobProvidersAndHosts != null) {
        this.oobProviders = new List<IContractProvider>();
        foreach (var oobProviderAndHost in oobProvidersAndHosts) {
          var oobProvider = oobProviderAndHost.Key;
          var oobHost = oobProviderAndHost.Value;
          this.oobProviders.Add(oobProvider);
          IUnit oobUnit = oobProvider.Unit;
          this.mapperForOobToPrimary.Add(oobProvider, new MappingMutator(host, primaryUnit, oobUnit));
          this.mapperForPrimaryToOob.Add(oobProvider, new MappingMutator(oobHost, oobUnit, primaryUnit));
        }
      }
    }

    #region IContractProvider Members

    /// <summary>
    /// Returns the loop contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="loop">An object that might have been associated with a loop contract. This can be any kind of object.</param>
    /// <returns></returns>
    public ILoopContract/*?*/ GetLoopContractFor(object loop) {
      throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the method contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="method">An object that might have been associated with a method contract. This can be any kind of object.</param>
    /// <returns></returns>
    public IMethodContract/*?*/ GetMethodContractFor(object method) {

      IMethodContract contract = this.underlyingContractProvider.GetMethodContractFor(method);
      if (contract != null) return contract == ContractDummy.MethodContract ? null : contract;

      MethodContract result = new MethodContract();
      IMethodContract primaryContract = this.primaryProvider.GetMethodContractFor(method);
      bool found = false;
      if (primaryContract != null) {
        found = true;
        ContractHelper.AddMethodContract(result, primaryContract);
      }
      if (this.oobProviders != null) {
        foreach (var oobProvider in this.oobProviders) {

          IMethodReference methodReference = method as IMethodReference;
          if (methodReference == null) continue; // REVIEW: Is there anything else it could be and still find a contract for it?

          MappingMutator primaryToOobMapper = this.mapperForPrimaryToOob[oobProvider];
          var oobMethod = primaryToOobMapper.Visit(methodReference);
          
          if (oobMethod == null) continue;

          var oobContract = oobProvider.GetMethodContractFor(oobMethod);

          if (oobContract == null) continue;

          MappingMutator oobToPrimaryMapper = this.mapperForOobToPrimary[oobProvider];
          oobContract = oobToPrimaryMapper.Visit(oobContract);
          ContractHelper.AddMethodContract(result, oobContract);
          found = true;

        }
      }

      // always cache so we don't try to extract more than once
      if (found) {
        this.underlyingContractProvider.AssociateMethodWithContract(method, result);
        return result;
      } else {
        this.underlyingContractProvider.AssociateMethodWithContract(method, ContractDummy.MethodContract);
        return null;
      }

    }

    /// <summary>
    /// Returns the triggers, if any, that have been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="quantifier">An object that might have been associated with triggers. This can be any kind of object.</param>
    /// <returns></returns>
    public IEnumerable<IEnumerable<IExpression>>/*?*/ GetTriggersFor(object quantifier) {
      throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the type contract, if any, that has been associated with the given object. Returns null if no association exits.
    /// </summary>
    /// <param name="type">An object that might have been associated with a type contract. This can be any kind of object.</param>
    /// <returns></returns>
    public ITypeContract/*?*/ GetTypeContractFor(object type) {

      ITypeContract contract = this.underlyingContractProvider.GetTypeContractFor(type);
      if (contract != null) return contract == ContractDummy.TypeContract ? null : contract;

      TypeContract result = new TypeContract();
      ITypeContract primaryContract = this.primaryProvider.GetTypeContractFor(type);
      bool found = false;
      if (primaryContract != null) {
        found = true;
        ContractHelper.AddTypeContract(result, primaryContract);
      }
      if (this.oobProviders != null) {
        foreach (var oobProvider in this.oobProviders) {
          var oobUnit = oobProvider.Unit;

          ITypeReference typeReference = type as ITypeReference;
          if (typeReference == null) continue; // REVIEW: Is there anything else it could be and still find a contract for it?

          MappingMutator primaryToOobMapper = this.mapperForPrimaryToOob[oobProvider];
          var oobType = primaryToOobMapper.Visit(typeReference);

          if (oobType == null) continue;

          var oobContract = oobProvider.GetTypeContractFor(oobType);

          if (oobContract == null) continue;

          MappingMutator oobToPrimaryMapper = this.mapperForOobToPrimary[oobProvider];
          oobContract = oobToPrimaryMapper.Visit(oobContract);
          ContractHelper.AddTypeContract(result, oobContract);
          found = true;

        }
      }

      // always cache so we don't try to extract more than once
      if (found) {
        this.underlyingContractProvider.AssociateTypeWithContract(type, result);
        return result;
      } else {
        this.underlyingContractProvider.AssociateTypeWithContract(type, ContractDummy.TypeContract);
        return null;
      }

    }

    /// <summary>
    /// A collection of methods that can be called in a way that provides tools with information about contracts.
    /// </summary>
    /// <value></value>
    public IContractMethods/*?*/ ContractMethods {
      get { return this.underlyingContractProvider.ContractMethods; }
    }

    /// <summary>
    /// The unit that this is a contract provider for. Intentional design:
    /// no provider works on more than one unit.
    /// </summary>
    /// <value></value>
    public IUnit/*?*/ Unit {
      get { return this.unit; }
    }

    #endregion
  }

}