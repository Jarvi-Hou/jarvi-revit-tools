using System;
using Autodesk.Revit.DB;

namespace JarviTools.Core
{
    internal static class TransactionSafety
    {
        internal static void Start(Transaction transaction, string operation)
        {
            if (transaction == null) throw new ArgumentNullException("transaction");
            EnsureStatus(
                transaction.Start(),
                TransactionStatus.Started,
                operation,
                "transaction",
                "start");
        }

        internal static void Start(TransactionGroup transactionGroup, string operation)
        {
            if (transactionGroup == null) throw new ArgumentNullException("transactionGroup");
            EnsureStatus(
                transactionGroup.Start(),
                TransactionStatus.Started,
                operation,
                "transaction group",
                "start");
        }

        internal static void Commit(Transaction transaction, string operation)
        {
            if (transaction == null) throw new ArgumentNullException("transaction");
            EnsureCommitted(transaction.Commit(), operation, "transaction");
        }

        internal static void Assimilate(TransactionGroup transactionGroup, string operation)
        {
            if (transactionGroup == null) throw new ArgumentNullException("transactionGroup");
            EnsureCommitted(transactionGroup.Assimilate(), operation, "transaction group");
        }

        internal static void RollBack(Transaction transaction, string operation)
        {
            if (transaction == null) throw new ArgumentNullException("transaction");
            EnsureStatus(
                transaction.RollBack(),
                TransactionStatus.RolledBack,
                operation,
                "transaction",
                "roll back");
        }

        internal static void RollBack(TransactionGroup transactionGroup, string operation)
        {
            if (transactionGroup == null) throw new ArgumentNullException("transactionGroup");
            EnsureStatus(
                transactionGroup.RollBack(),
                TransactionStatus.RolledBack,
                operation,
                "transaction group",
                "roll back");
        }

        private static void EnsureCommitted(
            TransactionStatus status,
            string operation,
            string transactionKind)
        {
            if (status == TransactionStatus.Committed) return;

            throw new InvalidOperationException(
                (string.IsNullOrWhiteSpace(operation) ? "Revit operation" : operation) +
                " did not commit its " + transactionKind + ". Status: " + status + ".");
        }

        private static void EnsureStatus(
            TransactionStatus actual,
            TransactionStatus expected,
            string operation,
            string transactionKind,
            string verb)
        {
            if (actual == expected) return;

            throw new InvalidOperationException(
                (string.IsNullOrWhiteSpace(operation) ? "Revit operation" : operation) +
                " could not " + verb + " its " + transactionKind +
                ". Expected: " + expected + "; actual: " + actual + ".");
        }
    }
}
