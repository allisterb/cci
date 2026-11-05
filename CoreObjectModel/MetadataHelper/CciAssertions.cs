// Silvergun: ADR 0022 Stage 1 (docs/agent/tasks/cci-assertions-stage1.md). Not upstream code.
//
// CCI's 96 Debug.Assert/Debug.Fail and 1,232 live Contract.Assert/Contract.Assume calls route a failure to
// Environment.FailFast on .NET: no exception, no catch, the process dies. This file contains that centrally, with no
// edit to the call sites:
//   - every Debug.* call in the assembly rebinds to CciDebug through the global alias below;
//   - a module initializer marks every Contract failure handled (or unwound, in Throw mode).
// Either way the failure is recorded, so a degraded read can be reported instead of silently looking clean.

global using Debug = Microsoft.Cci.CciDebug;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Microsoft.Cci {

  /// <summary>Which assertion channel failed.</summary>
  public enum CciAssertionKind {
    DebugAssert,
    DebugFail,
    ContractAssert,
    ContractAssume,
    OtherContract,
  }

  /// <summary>What a failed assertion does.</summary>
  public enum CciAssertionMode {
    /// <summary>Record it and continue past it: Release semantics, made observable. The default.</summary>
    RecordAndContinue,
    /// <summary>Record it and throw: Debug.* throws <see cref="CciAssertionException"/>, a contract unwinds.</summary>
    Throw,
  }

  /// <summary>One failed assertion.</summary>
  public sealed class CciAssertionRecord {
    internal CciAssertionRecord(CciAssertionKind kind, string message, string condition) {
      this.Kind = kind;
      this.Message = message;
      this.Condition = condition;
    }

    public CciAssertionKind Kind { get; }
    public string Message { get; }
    public string Condition { get; }

    public override string ToString() {
      return this.Condition == null ? this.Kind + ": " + this.Message : this.Kind + ": " + this.Message + " (" + this.Condition + ")";
    }
  }

  /// <summary>Thrown by a failed <see cref="CciDebug"/> assertion in <see cref="CciAssertionMode.Throw"/> mode.</summary>
  public sealed class CciAssertionException : Exception {
    public CciAssertionException(CciAssertionRecord record)
      : base("CCI assertion failed: " + record) {
      this.Record = record;
    }

    public CciAssertionRecord Record { get; }
  }

  /// <summary>
  /// The failures counted while it is open, on the logical flow that opened it (an <see cref="AsyncLocal{T}"/> chain,
  /// so nested scopes all count and other threads' failures are not seen). Keeps the first
  /// <see cref="MaxRecords"/> records. Dispose to close; scopes close in reverse order of opening.
  /// </summary>
  public sealed class CciAssertionScope : IDisposable {
    public const int MaxRecords = 16;

    readonly CciAssertionScope parent;
    readonly List<CciAssertionRecord> records = new List<CciAssertionRecord>();
    int count;
    bool disposed;

    internal CciAssertionScope(CciAssertionScope parent) {
      this.parent = parent;
    }

    /// <summary>The failures recorded while this scope was open.</summary>
    public int Count {
      get { return Volatile.Read(ref this.count); }
    }

    /// <summary>The first <see cref="MaxRecords"/> of them, in order.</summary>
    public IReadOnlyList<CciAssertionRecord> Records {
      get { lock (this.records) return this.records.ToArray(); }
    }

    internal CciAssertionScope Parent {
      get { return this.parent; }
    }

    internal void Add(CciAssertionRecord record) {
      Interlocked.Increment(ref this.count);
      lock (this.records) {
        if (this.records.Count < MaxRecords) this.records.Add(record);
      }
    }

    public void Dispose() {
      if (this.disposed) return;
      this.disposed = true;
      CciAssertions.Close(this);
    }
  }

  /// <summary>
  /// The policy for CCI's failed assertions (ADR 0022 Stage 1): <see cref="Mode"/>, the process-wide
  /// <see cref="Total"/>, and <see cref="BeginScope"/> for per-read counts. Never lets a failure reach
  /// <c>Environment.FailFast</c>.
  /// </summary>
  public static class CciAssertions {
    /// <summary>Set to <c>throw</c> to start in <see cref="CciAssertionMode.Throw"/> mode.</summary>
    public const string ModeVariable = "SILVERGUN_CCI_ASSERTIONS";

    static readonly AsyncLocal<CciAssertionScope> current = new AsyncLocal<CciAssertionScope>();
    static long total;
    static int installed;
    static volatile CciAssertionMode mode = InitialMode();

    /// <summary>What a failed assertion does. Process-wide.</summary>
    public static CciAssertionMode Mode {
      get { return mode; }
      set { mode = value; }
    }

    /// <summary>Every failure recorded in this process.</summary>
    public static long Total {
      get { return Interlocked.Read(ref total); }
    }

    /// <summary>True once the contract handler is subscribed (it is from the first use of this assembly).</summary>
    public static bool IsInstalled {
      get { return Volatile.Read(ref installed) == 1; }
    }

    /// <summary>Opens a scope that counts the failures raised on this logical flow until it is disposed.</summary>
    public static CciAssertionScope BeginScope() {
      Install();
      var scope = new CciAssertionScope(current.Value);
      current.Value = scope;
      return scope;
    }

    /// <summary>Subscribes the contract handler once. Runs from the module initializer; idempotent.</summary>
    // CA2255 warns that module initializers belong in applications. Here it is the point: the handler must be in place
    // before any CCI code runs, in every process that loads CCI, whatever its host does.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Install() {
      if (Interlocked.Exchange(ref installed, 1) == 1) return;
      Contract.ContractFailed += OnContractFailed;
    }

    internal static void Close(CciAssertionScope scope) {
      // Only the innermost open scope of this flow is replaced; closing out of order leaves the chain as it is.
      if (ReferenceEquals(current.Value, scope)) current.Value = scope.Parent;
    }

    internal static CciAssertionRecord Report(CciAssertionKind kind, string message, string condition) {
      var record = new CciAssertionRecord(kind, message ?? string.Empty, condition);
      Interlocked.Increment(ref total);
      for (var scope = current.Value; scope != null; scope = scope.Parent)
        scope.Add(record);
      return record;
    }

    static void OnContractFailed(object sender, ContractFailedEventArgs e) {
      var kind = e.FailureKind == ContractFailureKind.Assert ? CciAssertionKind.ContractAssert
        : e.FailureKind == ContractFailureKind.Assume ? CciAssertionKind.ContractAssume
        : CciAssertionKind.OtherContract;
      Report(kind, e.Message, e.Condition);
      if (mode == CciAssertionMode.Throw) e.SetUnwind();
      else e.SetHandled();
    }

    static CciAssertionMode InitialMode() {
      var value = Environment.GetEnvironmentVariable(ModeVariable);
      return string.Equals(value, "throw", StringComparison.OrdinalIgnoreCase) ? CciAssertionMode.Throw : CciAssertionMode.RecordAndContinue;
    }
  }

  /// <summary>
  /// What <c>Debug</c> means inside CCI (the global alias above). Same signatures and the same
  /// <c>[Conditional("DEBUG")]</c> as <see cref="System.Diagnostics.Debug"/>, so a call is evaluated exactly when it
  /// was before; a failure is recorded and continues, or throws in <see cref="CciAssertionMode.Throw"/> mode.
  /// </summary>
  public static class CciDebug {
    [Conditional("DEBUG")]
    public static void Assert(bool condition) {
      if (!condition) Failed(CciAssertionKind.DebugAssert, string.Empty, null);
    }

    [Conditional("DEBUG")]
    public static void Assert(bool condition, string message) {
      if (!condition) Failed(CciAssertionKind.DebugAssert, message, null);
    }

    [Conditional("DEBUG")]
    public static void Assert(bool condition, string message, string detailMessage) {
      if (!condition) Failed(CciAssertionKind.DebugAssert, message, detailMessage);
    }

    [Conditional("DEBUG")]
    public static void Fail(string message) {
      Failed(CciAssertionKind.DebugFail, message, null);
    }

    [Conditional("DEBUG")]
    public static void Fail(string message, string detailMessage) {
      Failed(CciAssertionKind.DebugFail, message, detailMessage);
    }

    static void Failed(CciAssertionKind kind, string message, string detail) {
      var record = CciAssertions.Report(kind, message, detail);
      if (CciAssertions.Mode == CciAssertionMode.Throw) throw new CciAssertionException(record);
    }
  }
}
