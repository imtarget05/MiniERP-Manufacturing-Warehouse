using Xunit;

// All integration tests share a single Oracle schema; disabling parallelization
// keeps stock top-ups and order completions strictly sequential so one test
// class can never observe mid-flight stock mutated by the other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]