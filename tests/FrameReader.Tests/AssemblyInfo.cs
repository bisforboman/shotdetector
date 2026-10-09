// FFmpeg's libraries load once per process, and one test checks the failure before anything loads them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
