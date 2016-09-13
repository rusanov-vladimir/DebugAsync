using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Samples.Debugging.CorDebug;
using Microsoft.Samples.Debugging.MdbgEngine;

namespace AsyncDebug {
    class Program {
        private static MDbgEngine _engine;
        static void Main(string[] args) {
            _engine = new MDbgEngine();
            string sourceText = @"
/*1*/ using System;
/*2*/ using System.Threading.Tasks;
/*3*/  
/*4*/ class C
/*5*/ {
/*6*/     public static void Main() {
/*7*/         Console.WriteLine(""Press Enter"");Console.ReadLine();
/*8*/         var instance = new Instance(); //<------------------Breaks here
/*9*/         Console.WriteLine(""Executing method Start""); instance.Start().Wait(); Console.WriteLine(""Executed"");
/*10*/    }
/*11*/ }
/*12*/ class Instance
/*13*/ {
/*14*/    public static async Task F() { for(var i=0; i<100; i++) { Console.WriteLine(i); await Task.Delay(100); } }
/*15*/    public async Task Start() {//<------------------Breaks here
/*16*/       var z = ""test"";
/*17*/       Console.WriteLine(z);
/*18*/       var x = 10;Console.WriteLine(x);
/*19*/       await F();
/*20*/    }
/*21*/ }
";

            string programName = "MyProgram.exe";
            string pdbName = "MyProgram.pdb";

            //Get solution 
            var solution = createSolution(sourceText);

            //Compile code and emit to disk
            CompileAndEmitToDisk(solution, programName, pdbName);

            var p = Process.Start(new ProcessStartInfo {FileName = programName, });
            Thread.Sleep(100); //let execution continue till "Press Enter"
            _engine.Attach(p.Id, RuntimeEnvironment.GetSystemVersion());
            _engine.Processes.Active.Go().WaitOne();
            _engine.Processes.Active.PostDebugEvent += Active_PostDebugEvent;
            SetBreakpointOnStartMethod();

            _engine.Processes.Active.Go().WaitOne();
            ShowLocation();
            GetLocalVariables();

            _engine.Processes.Active.Go().WaitOne();
            ShowLocation();
            GetLocalVariables();

            InfiniteLoopMenu();
            
        }

        private static void InfiniteLoopMenu() {
            while (true) {
                var c = Console.ReadLine();
                switch (c) {
                    case "e":
                    case "exit":
                        return;
                    case "l":
                    case "location":
                        ShowLocation();
                        break;
                    case "g":
                    case "go":
                        _engine.Processes.Active.Go();
                        break;
                    case "s":
                    case "stop":
                        _engine.Processes.Active.AsyncStop().WaitOne();
                        break;
                    case "si":
                    case "stepinto":
                        _engine.Processes.Active.StepInto(false);
                        break;
                    case "so":
                    case "stepover":
                        _engine.Processes.Active.StepOver(false);
                        break;
                    case "v":
                    case "variables":
                        GetLocalVariables();

                        break;
                }
                Thread.Sleep(500);
            }
        }

        private static void GetLocalVariables() {
            Console.WriteLine("------Variables-------");
            var f = _engine.Processes.Active.Threads.Active.CurrentFrame;
            var activeLocalVars = f.Function.GetActiveLocalVars(f);
            var arguments = f.Function.GetArguments(f);
            foreach (MDbgValue var in activeLocalVars.Union(arguments)) {
                Console.WriteLine($"{var.Name}: {var.GetStringValue(true)}");
            }
            Console.WriteLine("----------------------");
        }

        private static void SetBreakpointOnStartMethod() {
            _engine.Processes.Active.Breakpoints.CreateBreakpoint("MyDocument.cs", 9);
            _engine.Processes.Active.Breakpoints.CreateBreakpoint("MyDocument.cs", 16);
            //_engine.Processes.Active.Breakpoints.CreateBreakpoint("MyProgram", "Instance", "Start", 0); //setting bp inside rewritten method
        }

        private static void Active_PostDebugEvent(object sender, CustomPostCallbackEventArgs e)
        {
            switch (e.CallbackType) {
                case ManagedCallbackType.OnModuleLoad:
                    var module = ((CorModuleEventArgs) e.CallbackArgs).Module;
                    if (module.Name == "MyProgram.exe") {
                        module.SetJmcStatus(true, null);
                    }
                    break;
                case ManagedCallbackType.OnProcessExit:
                    Environment.Exit(0);
                    break;

                    
            }
        }

        private static void ShowLocation() {
            if (_engine.Processes.Active.Threads.Active.CurrentSourcePosition == null) {
                Console.WriteLine("No source available");
                return;
            }
            var fileName = _engine.Processes.Active.Threads.Active.CurrentSourcePosition.Path;
            var line = _engine.Processes.Active.Threads.Active.CurrentSourcePosition.Line - 1;
            Console.WriteLine("------Location-------");
            Console.WriteLine($"{fileName}: {line}");
            Console.WriteLine(_engine.Processes.Active.Threads.Active.CurrentFrame.Function.FullName);
            Console.WriteLine("---------------------");
        }

        private static Solution createSolution(string text) {
            var tree = CSharpSyntaxTree.ParseText(text);
            var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            var adHockWorkspace = new AdhocWorkspace();

            var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication, platform: Platform.X86);
            var project = adHockWorkspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "MyProject", "MyProject", "C#", metadataReferences: new List<MetadataReference>() {mscorlib}, compilationOptions: options));

            adHockWorkspace.AddDocument(project.Id, "MyDocument.cs", SourceText.From(text, System.Text.UTF8Encoding.UTF8));
            return adHockWorkspace.CurrentSolution;
        }

        private static void CompileAndEmitToDisk(Solution solution, string programName, string pdbName) {
            var compilation = solution.Projects.Single().GetCompilationAsync().Result;

            var emitResult = compilation.Emit(programName, pdbName);
            if (!emitResult.Success) {
                throw new InvalidOperationException("Errors in compilation: " + emitResult.Diagnostics.Count());
            }
        }
    }
}