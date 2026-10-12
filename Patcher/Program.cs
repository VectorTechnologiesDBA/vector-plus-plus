using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Patcher
{
	/// <summary>
	/// Build steps for the wire-pod version of the Vector app (driven by build.ps1):
	///   extract &lt;apk&gt; &lt;dir&gt;                     copy assemblies/*.dll out of the APK
	///   patch &lt;refDir&gt; &lt;WirePod.dll&gt; &lt;outDir&gt;    patch chewie.Droid.dll to call into WirePod.dll, turn off analytics in ChewieResources.dll
	///   pack &lt;apk&gt; &lt;outApk&gt; &lt;entry=file&gt;...     copy the APK, replacing/adding stored entries, dropping the old signature
	///   align &lt;inApk&gt; &lt;outApk&gt;                 zipalign-equivalent (4 bytes, 4096 for stored .so)
	///   verify &lt;origApk&gt; &lt;newApk&gt; &lt;entry&gt;...    check every other entry is unchanged and stored entries are aligned
	/// </summary>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			try
			{
				switch (args.Length > 0 ? args[0] : "")
				{
				case "extract":
					Program.Extract(args[1], args[2]);
					return 0;
				case "patch":
					Program.PatchAssemblies(args[1], args[2], args[3]);
					return 0;
				case "pack":
					Program.Pack(args[1], args[2], args.Skip(3).ToArray());
					return 0;
				case "align":
					ApkZip.Rewrite(args[1], args[2], new Dictionary<string, byte[]>(), (string name) => false);
					Console.WriteLine("Aligned " + args[2]);
					return 0;
				case "verify":
					return Program.Verify(args[1], args[2], new HashSet<string>(args.Skip(3)));
				}
				Console.Error.WriteLine("usage: Patcher extract|patch|pack|align|verify ... (see Program.cs)");
				return 2;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine("error: " + ex);
				return 1;
			}
		}

		private static void Extract(string apk, string outDir)
		{
			Directory.CreateDirectory(outDir);
			using (ZipArchive zipArchive = ZipFile.OpenRead(apk))
			{
				foreach (ZipArchiveEntry zipArchiveEntry in zipArchive.Entries.Where((ZipArchiveEntry e) => e.FullName.StartsWith("assemblies/") && e.FullName.EndsWith(".dll")))
				{
					zipArchiveEntry.ExtractToFile(Path.Combine(outDir, zipArchiveEntry.Name), true);
				}
			}
			Console.WriteLine("Extracted assemblies to " + outDir);
		}

		private static void PatchAssemblies(string refDir, string wirePodPath, string outDir)
		{
			AssemblyResolver assemblyResolver = new AssemblyResolver();
			assemblyResolver.UseGAC = false;
			assemblyResolver.FindExactMatch = false;
			assemblyResolver.PreSearchPaths.Add(refDir);
			ModuleContext moduleContext = new ModuleContext(assemblyResolver);
			assemblyResolver.DefaultModuleContext = moduleContext;

			ModuleDefMD chewie = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(refDir, "chewie.Droid.dll")), moduleContext);
			// WirePod.dll references chewie.Droid types (e.g. RobotCommunicationService). Resolve those to this module so
			// imported signatures use chewie's own type definitions rather than references to itself.
			assemblyResolver.AddToCache(chewie);
			ModuleDefMD wirePod = ModuleDefMD.Load(File.ReadAllBytes(wirePodPath), moduleContext);
			// The app's trimmed mscorlib has no AssemblyVersionAttribute, so csc leaves 0.0.0.0. Stamp it before chewie references it.
			wirePod.Assembly.Version = new Version(1, 0, 0, 0);
			Importer importer = new Importer(chewie, ImporterOptions.TryToUseDefs);

			TypeDef httpService = Program.FindType(wirePod, "WirePod.WirePodHttpService");
			TypeDef config = Program.FindType(wirePod, "WirePod.WirePodConfig");
			IMethod wrap = importer.Import(Program.FindMethod(httpService, "Wrap"));
			IMethod sessionCertUrl = importer.Import(Program.FindMethod(config, "SessionCertUrl"));
			IMethod otaUrl = importer.Import(Program.FindMethod(config, "OtaUrl"));

			// 1-3. Route the Anki account, email-subscription and location services through WirePodHttpService.
			foreach (string typeName in new string[]
			{
				"chewie.Services.AccountService",
				"chewie.Services.EmailSubscriptionService",
				"chewie.Services.LocationAutoCompletionService"
			})
			{
				MethodDef configure = Program.FindMethod(Program.FindType(chewie, typeName), "Configure");
				Parameter parameter = configure.Parameters[1];
				Program.Expect(parameter.Type.FullName == "core.Services.Http.IHttpService", configure.FullName + ": first argument is not IHttpService");
				IList<Instruction> instructions = configure.Body.Instructions;
				instructions.Insert(0, OpCodes.Ldarg_1.ToInstruction());
				instructions.Insert(1, OpCodes.Call.ToInstruction(wrap));
				instructions.Insert(2, OpCodes.Starg_S.ToInstruction(parameter));
				Console.WriteLine("Patched " + configure.FullName);
			}

			// 4. RobotCommunicationService.OtaUrl: the URL the robot downloads firmware from. The field is read
			//    from EndpointSettings once at startup, so hook the getter to always reflect the current server.
			MethodDef getOtaUrl = Program.FindMethod(Program.FindType(chewie, "chewie.Services.RobotCommunicationService"), "get_OtaUrl");
			Program.HookReturnValue(getOtaUrl, otaUrl);
			Console.WriteLine("Patched " + getOtaUrl.FullName);

			// 5. "https://session-certs.token.global.anki-services.com/vic/" + esn -> WirePodConfig.SessionCertUrl(that, esn)
			int num = 0;
			foreach (MethodDef methodDef in chewie.GetTypes().SelectMany((TypeDef t) => t.Methods).Where((MethodDef m) => m.HasBody))
			{
				IList<Instruction> instructions2 = methodDef.Body.Instructions;
				for (int i = 0; i < instructions2.Count; i++)
				{
					if (instructions2[i].OpCode != OpCodes.Ldstr || !((string)instructions2[i].Operand).Contains("session-certs.token.global.anki-services.com"))
					{
						continue;
					}
					Instruction instruction = instructions2.Skip(i + 1).Take(8).FirstOrDefault((Instruction x) => x.OpCode == OpCodes.Call && x.Operand is IMethod && ((IMethod)x.Operand).FullName == "System.String System.String::Concat(System.String,System.String)");
					Program.Expect(instruction != null, "no string.Concat after the session-certs URL in " + methodDef.FullName);
					instruction.Operand = sessionCertUrl;
					num++;
					Console.WriteLine("Patched session cert URL in " + methodDef.FullName);
				}
			}
			Program.Expect(num == 1, string.Format("expected 1 session-certs URL, patched {0}", num));

			// 6. RobotCommunicationService.Initialize: hand the service to RobotSetup, which listens for robots that
			//    can't reach their server and asks wire-pod to set them up over SSH.
			//    The reference is built by hand: its parameter type is chewie's own RobotCommunicationService, and the
			//    importer would otherwise write that as a reference into WirePod, which the runtime can't load.
			TypeDef robotCommunicationService = Program.FindType(chewie, "chewie.Services.RobotCommunicationService");
			Program.FindMethod(Program.FindType(wirePod, "WirePod.RobotSetup"), "Attach");
			MemberRef attach = new MemberRefUser(chewie, "Attach", MethodSig.CreateStatic(chewie.CorLibTypes.Void, new ClassSig(robotCommunicationService)), importer.Import(Program.FindType(wirePod, "WirePod.RobotSetup")));
			MethodDef initialize = Program.FindMethod(robotCommunicationService, "Initialize");
			Program.CallWithThisBeforeReturn(initialize, attach);
			Console.WriteLine("Patched " + initialize.FullName);

			// 7. Turn off analytics (DAS). Its uploads go to Anki's suspended AWS account and fail; failed files are
			//    retried forever and every failure is logged into a new file to upload. The resulting flood of threads
			//    makes the app's old Mono runtime deadlock against Android's thread list lock (seen on Android 15).
			ModuleDefMD chewieResources = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(refDir, "ChewieResources.dll")), moduleContext);
			Program.EditJsonResource(chewieResources, "ChewieResources.Config.DASConfig.json", delegate(JsonObject json)
			{
				json["ShouldSyncNetwork"] = false;
				// No enabled levels: nothing is queued to disk for upload. Console logging is unaffected.
				json["LogLevels"] = new JsonArray();
			});

			Directory.CreateDirectory(outDir);
			// Keep metadata tokens stable; nothing else in the app has to change.
			chewie.Write(Path.Combine(outDir, "chewie.Droid.dll"), Program.PreservingWriterOptions(chewie));
			chewieResources.Write(Path.Combine(outDir, "ChewieResources.dll"), Program.PreservingWriterOptions(chewieResources));
			wirePod.Write(Path.Combine(outDir, "WirePod.dll"));
			Program.CheckReferences(outDir, refDir);
		}

		private static ModuleWriterOptions PreservingWriterOptions(ModuleDef module)
		{
			ModuleWriterOptions moduleWriterOptions = new ModuleWriterOptions(module);
			moduleWriterOptions.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
			return moduleWriterOptions;
		}

		/// <summary>Rewrites an embedded JSON resource in place, keeping its name, attributes and UTF-8 BOM.</summary>
		private static void EditJsonResource(ModuleDef module, string name, Action<JsonObject> edit)
		{
			int num = module.Resources.ToList().FindIndex((Resource r) => r.Name == name);
			Program.Expect(num >= 0, "resource not found: " + name);
			EmbeddedResource embeddedResource = (EmbeddedResource)module.Resources[num];
			string text = Encoding.UTF8.GetString(embeddedResource.CreateReader().ToArray()).TrimStart('﻿');
			JsonObject jsonObject = JsonNode.Parse(text).AsObject();
			edit(jsonObject);
			string text2 = jsonObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
			byte[] bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text2)).ToArray();
			module.Resources[num] = new EmbeddedResource(embeddedResource.Name, bytes, embeddedResource.Attributes);
			Console.WriteLine("Patched resource " + name + ": " + text2.Replace(Environment.NewLine, " "));
		}

		/// <summary>
		/// Calls hook(this) at the end of a void instance method. Each ret becomes ldarg.0 followed by the call and a
		/// new ret, so branches that targeted the old ret still go through the hook.
		/// </summary>
		private static void CallWithThisBeforeReturn(MethodDef method, IMethod hook)
		{
			Program.Expect(method.ReturnType.ElementType == ElementType.Void && !method.IsStatic, method.FullName + " is not a void instance method");
			IList<Instruction> instructions = method.Body.Instructions;
			foreach (Instruction instruction in instructions.Where((Instruction x) => x.OpCode == OpCodes.Ret).ToList())
			{
				instruction.OpCode = OpCodes.Ldarg_0;
				instruction.Operand = null;
				int num = instructions.IndexOf(instruction);
				instructions.Insert(num + 1, OpCodes.Call.ToInstruction(hook));
				instructions.Insert(num + 2, OpCodes.Ret.ToInstruction());
			}
		}

		/// <summary>
		/// Passes every return value of method through hook(value). Each ret becomes the call and a new ret
		/// follows it, so branches that targeted the old ret still go through the hook.
		/// </summary>
		private static void HookReturnValue(MethodDef method, IMethod hook)
		{
			IList<Instruction> instructions = method.Body.Instructions;
			foreach (Instruction instruction in instructions.Where((Instruction x) => x.OpCode == OpCodes.Ret).ToList())
			{
				instruction.OpCode = OpCodes.Call;
				instruction.Operand = hook;
				instructions.Insert(instructions.IndexOf(instruction) + 1, OpCodes.Ret.ToInstruction());
			}
		}

		/// <summary>
		/// Everything WirePod.dll uses must exist in the app's (linker-trimmed) assemblies, and everything the patched
		/// chewie.Droid.dll uses from WirePod.dll must exist there; otherwise the app would crash when it reaches that code.
		/// </summary>
		private static void CheckReferences(string outDir, string refDir)
		{
			AssemblyResolver assemblyResolver = new AssemblyResolver();
			assemblyResolver.UseGAC = false;
			assemblyResolver.FindExactMatch = false;
			assemblyResolver.PreSearchPaths.Add(outDir);
			assemblyResolver.PreSearchPaths.Add(refDir);
			ModuleContext moduleContext = new ModuleContext(assemblyResolver);
			assemblyResolver.DefaultModuleContext = moduleContext;
			List<string> list = new List<string>();
			int num = 0;
			foreach (string text in new string[] { "WirePod.dll", "chewie.Droid.dll" })
			{
				ModuleDefMD moduleDefMD = ModuleDefMD.Load(Path.Combine(outDir, text), moduleContext);
				bool all = text == "WirePod.dll";
				if (moduleDefMD.GetAssemblyRefs().Any((AssemblyRef a) => a.Name == moduleDefMD.Assembly.Name))
				{
					list.Add(text + ": references its own assembly");
				}
				// Both of these load in dnlib but crash Mono with a TypeLoadException ("in assembly ''").
				foreach (TypeRef typeRef2 in moduleDefMD.GetTypeRefs())
				{
					if (typeRef2.ResolutionScope is ModuleRef)
					{
						list.Add(text + ": type " + typeRef2.FullName + " is referenced through module " + typeRef2.ResolutionScope);
					}
					if (moduleDefMD.Find(typeRef2.FullName, false) != null)
					{
						list.Add(text + ": references its own type " + typeRef2.FullName);
					}
				}
				foreach (TypeRef typeRef in moduleDefMD.GetTypeRefs().Where((TypeRef t) => all || Program.IsWirePod(t)))
				{
					num++;
					if (typeRef.Resolve() == null)
					{
						list.Add(text + ": type " + typeRef.FullName);
					}
				}
				for (uint num2 = 1U; num2 <= moduleDefMD.TablesStream.MemberRefTable.Rows; num2 += 1U)
				{
					MemberRef memberRef = moduleDefMD.ResolveMemberRef(num2);
					if (!all && !Program.IsWirePod(memberRef.DeclaringType))
					{
						continue;
					}
					num++;
					if (memberRef.Resolve() == null)
					{
						list.Add(text + ": member " + memberRef.FullName);
					}
				}
			}
			foreach (string text2 in list)
			{
				Console.Error.WriteLine("  unresolved " + text2);
			}
			Program.Expect(list.Count == 0, "some references don't exist in the app's assemblies");
			Console.WriteLine(string.Format("All {0} WirePod references resolve against the app's assemblies", num));
		}

		private static bool IsWirePod(ITypeDefOrRef type)
		{
			return type != null && type.DefinitionAssembly != null && type.DefinitionAssembly.Name == "WirePod";
		}

		private static void Pack(string apk, string outApk, string[] entries)
		{
			Dictionary<string, byte[]> dictionary = new Dictionary<string, byte[]>();
			foreach (string text in entries)
			{
				int num = text.IndexOf('=');
				dictionary[text.Substring(0, num)] = File.ReadAllBytes(text.Substring(num + 1));
			}
			ApkZip.Rewrite(apk, outApk, dictionary, new Func<string, bool>(ApkZip.IsV1SignatureFile));
			Console.WriteLine("Packed " + outApk);
		}

		private static int Verify(string originalApk, string newApk, HashSet<string> changed)
		{
			List<ApkZip.Entry> list = ApkZip.ReadEntries(originalApk);
			Dictionary<string, ApkZip.Entry> dictionary = ApkZip.ReadEntries(newApk).ToDictionary((ApkZip.Entry e) => e.Name);
			int num = 0;
			int num2 = 0;
			foreach (ApkZip.Entry entry in list)
			{
				if (changed.Contains(entry.Name) || ApkZip.IsV1SignatureFile(entry.Name))
				{
					continue;
				}
				ApkZip.Entry entry2;
				if (!dictionary.TryGetValue(entry.Name, out entry2) || entry2.Crc32 != entry.Crc32 || entry2.UncompressedSize != entry.UncompressedSize)
				{
					Console.Error.WriteLine("changed or missing: " + entry.Name);
					num2++;
				}
				else
				{
					num++;
				}
			}
			foreach (string text in changed)
			{
				if (!dictionary.ContainsKey(text))
				{
					Console.Error.WriteLine("missing: " + text);
					num2++;
				}
			}
			foreach (ApkZip.Entry entry3 in dictionary.Values.Where((ApkZip.Entry e) => e.Method == 0))
			{
				if (entry3.DataOffset % (long)ApkZip.AlignmentFor(entry3.Name) != 0L)
				{
					Console.Error.WriteLine("not aligned: " + entry3.Name);
					num2++;
				}
			}
			Console.WriteLine(string.Format("{0} original entries identical, {1} replaced/added, {2} problems", num, changed.Count, num2));
			return (num2 == 0) ? 0 : 1;
		}

		private static TypeDef FindType(ModuleDef module, string fullName)
		{
			TypeDef typeDef = module.Find(fullName, false);
			Program.Expect(typeDef != null, "type not found: " + fullName);
			return typeDef;
		}

		private static MethodDef FindMethod(TypeDef type, string name)
		{
			List<MethodDef> list = type.Methods.Where((MethodDef m) => m.Name == name).ToList();
			Program.Expect(list.Count == 1, string.Format("expected one {0}.{1}, found {2}", type.FullName, name, list.Count));
			return list[0];
		}

		private static void Expect(bool condition, string message)
		{
			if (!condition)
			{
				throw new InvalidOperationException(message);
			}
		}
	}
}
