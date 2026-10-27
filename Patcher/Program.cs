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
	///   fonts &lt;apk&gt; &lt;outDir&gt;                    copy res/font/*.otf without their AAT tables (see FontFix.cs)
	///   pack &lt;apk&gt; &lt;outApk&gt; &lt;entry=file&gt;...     copy the APK, replacing/adding stored entries, dropping the old signature
	///                                              (@dir adds every file under dir, named by its path relative to dir)
	///   align &lt;inApk&gt; &lt;outApk&gt;                 zipalign-equivalent (4 bytes, 4096 for stored .so)
	///   verify &lt;origApk&gt; &lt;newApk&gt; &lt;entry=file&gt;...    check every other entry is unchanged and stored entries are aligned
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
				case "fonts":
					FontFix.FixApkFonts(args[1], args[2]);
					return 0;
				case "pack":
					Program.Pack(args[1], args[2], args.Skip(3).ToArray());
					return 0;
				case "align":
					ApkZip.Rewrite(args[1], args[2], new Dictionary<string, byte[]>(), (string name) => false);
					Console.WriteLine("Aligned " + args[2]);
					return 0;
				case "verify":
					return Program.Verify(args[1], args[2], new HashSet<string>(Program.ExpandEntries(args.Skip(3)).Keys));
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
			Program.EditJsonResource(chewieResources, "ChewieResources.Config.DASConfig.json", delegate(JsonNode node)
			{
				JsonObject json = node.AsObject();
				json["ShouldSyncNetwork"] = false;
				// No enabled levels: nothing is queued to disk for upload. Console logging is unaffected.
				json["LogLevels"] = new JsonArray();
			});

			// 8. Settings: an "Extra features" row above About; WirePod.Extras opens the Java screens (classes3.dex).
			Program.EditJsonResource(chewieResources, "ChewieResources.Settings.MainSettingsData.json", delegate(JsonNode node)
			{
				JsonArray rows = node["MainSettingsTableObjs"].AsArray();
				int about = rows.ToList().FindIndex((JsonNode r) => (string)r["NavigationKey"] == "about");
				Program.Expect(about > 0, "no About row in MainSettingsData.json");
				rows.Insert(about, new JsonObject
				{
					["CellType"] = 1, ["Title"] = "Extra features", ["SubTitle"] = "", ["NavigationKey"] = "wirepod_extras",
					["IconImageKey"] = "settings_icon_preferences_mini", ["Height"] = 0.1
				});
				// Eanable hidden matrix green
				JsonNode matrix = node["EyeColorCollectionObjs"].AsArray().ToList().Single((JsonNode c) => (int)c["Id"] == 6);
				matrix["Name"] = "EyeColor.colorName.6";
				matrix["Enabled"] = true;
				matrix["EyeColor"]["R"] = 0.0;
				matrix["EyeColor"]["G"] = 1.0;
				matrix["EyeColor"]["B"] = 0.25;
				matrix["EyeColor"]["A"] = 1.0;
				// Custom eye color, backported from app 1.8 (EyeColorActivity), right below the preset eye colors (the header
				// row). MainSettingsViewModel.NetworkIndex is shifted to match (patch 14).
				rows.Insert(1, new JsonObject
				{
					["CellType"] = 1, ["Title"] = "Custom eye color", ["SubTitle"] = "", ["NavigationKey"] = "wirepod_eyecolor",
					["IconImageKey"] = "settings_icon_preferences_mini", ["Height"] = 0.1
				});
			});
			TypeDef extras = Program.FindType(wirePod, "WirePod.Extras");
			MethodDef onRowSelected = Program.FindMethod(Program.FindType(chewie, "chewie.Settings.MainSettingsViewModel"), "OnRowSelected");
			{
				// Task t = Extras.OpenSetting(navKey); if (t != null) return t;
				IList<Instruction> instructions3 = onRowSelected.Body.Instructions;
				Instruction first = instructions3[0];
				instructions3.Insert(0, OpCodes.Ldarg_1.ToInstruction());
				instructions3.Insert(1, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "OpenSetting"))));
				instructions3.Insert(2, OpCodes.Dup.ToInstruction());
				Instruction pop = OpCodes.Pop.ToInstruction();
				instructions3.Insert(3, OpCodes.Brfalse_S.ToInstruction(pop));
				instructions3.Insert(4, OpCodes.Ret.ToInstruction());
				instructions3.Insert(5, pop);
				onRowSelected.Body.SimplifyBranches();
				onRowSelected.Body.OptimizeBranches();
				Console.WriteLine("Patched " + onRowSelected.FullName);
			}

			// 9. Home: Drive and SSH tiles; HandleCellSelected asks Extras.OpenTile(CategoryList[index].Id) first.
			Program.EditJsonResource(chewieResources, "ChewieResources.ViewData.CategoryCardData.json", delegate(JsonNode node)
			{
				JsonArray tiles = node.AsArray();
				tiles.Add(new JsonObject { ["Id"] = "wirepod_drive", ["CategoryTitle"] = "Drive", ["CategoryIcon"] = "nav_icon_drive_sml", ["FeaturedIndex"] = 0, ["FeatureFlagId"] = "wirepod_drive", ["InfoCardIds"] = new JsonArray() });
				tiles.Add(new JsonObject { ["Id"] = "wirepod_ssh", ["CategoryTitle"] = "SSH", ["CategoryIcon"] = "nav_icon_ssh_sml", ["FeaturedIndex"] = 0, ["FeatureFlagId"] = "wirepod_ssh", ["InfoCardIds"] = new JsonArray() });
				tiles.Add(new JsonObject { ["Id"] = "wirepod_quick", ["CategoryTitle"] = "Quick Actions", ["CategoryIcon"] = "nav_icon_quickactions_sml", ["FeaturedIndex"] = 0, ["FeatureFlagId"] = "wirepod_quick", ["InfoCardIds"] = new JsonArray() });
			});
			MethodDef cellSelected = Program.FindMethod(Program.FindType(chewie, "chewie.Features.Home.HomeFlowModel"), "HandleCellSelected");
			{
				// The method starts with this.CategoryList[index].Id: copy those four instructions, then call the hook.
				IList<Instruction> instructions4 = cellSelected.Body.Instructions;
				Program.Expect(instructions4[0].OpCode == OpCodes.Ldarg_0 && instructions4[2].OpCode == OpCodes.Ldarg_1
					&& ((IMethod)instructions4[3].Operand).Name == "get_Item" && ((IMethod)instructions4[4].Operand).Name == "get_Id",
					cellSelected.FullName + " doesn't start with CategoryList[index].Id");
				Instruction first = instructions4[0];
				List<Instruction> load = instructions4.Take(5).Select((Instruction x) => new Instruction(x.OpCode, x.Operand)).ToList();
				for (int j = 0; j < load.Count; j++)
				{
					instructions4.Insert(j, load[j]);
				}
				instructions4.Insert(5, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "OpenTile"))));
				instructions4.Insert(6, OpCodes.Brfalse.ToInstruction(first));
				instructions4.Insert(7, OpCodes.Ret.ToInstruction());
				Console.WriteLine("Patched " + cellSelected.FullName);
			}

			// 10. ResourceHelper.GetResourceIdForIdentifier: fall back to Android's lookup for the new icons.
			MethodDef getResourceId = Program.FindMethod(Program.FindType(chewie, "chewie.Droid.Helpers.ResourceHelper"), "GetResourceIdForIdentifier");
			{
				IMethod resolve = importer.Import(Program.FindMethod(extras, "ResolveResource"));
				IList<Instruction> instructions5 = getResourceId.Body.Instructions;
				foreach (Instruction ret in instructions5.Where((Instruction x) => x.OpCode == OpCodes.Ret).ToList())
				{
					// ret -> ldarg.0; ldarg.1; call ResolveResource(id, identifier, type); ret (branches to ret still pass through)
					ret.OpCode = OpCodes.Ldarg_0;
					ret.Operand = null;
					int at = instructions5.IndexOf(ret);
					instructions5.Insert(at + 1, OpCodes.Ldarg_1.ToInstruction());
					instructions5.Insert(at + 2, OpCodes.Call.ToInstruction(resolve));
					instructions5.Insert(at + 3, OpCodes.Ret.ToInstruction());
				}
				Console.WriteLine("Patched " + getResourceId.FullName);
			}

			// 11. The Drive/SSH/Quick Actions tiles carry feature flags (their ids): ChewieFeatureFlagService.IsFeatureEnabled answers
			//     them from the Extra features switches, so Home hides them (and keeps its original layout) when it's off.
			MethodDef isFeatureEnabled = Program.FindMethod(Program.FindType(chewie, "chewie.Services.ChewieFeatureFlagService"), "IsFeatureEnabled");
			{
				IList<Instruction> instructions6 = isFeatureEnabled.Body.Instructions;
				Instruction first = instructions6[0];
				instructions6.Insert(0, OpCodes.Ldarg_1.ToInstruction());
				instructions6.Insert(1, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "IsExtrasFlag"))));
				instructions6.Insert(2, OpCodes.Brfalse.ToInstruction(first));
				instructions6.Insert(3, OpCodes.Ldarg_1.ToInstruction());
				instructions6.Insert(4, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "FeatureOn"))));
				instructions6.Insert(5, OpCodes.Ret.ToInstruction());
				Console.WriteLine("Patched " + isFeatureEnabled.FullName);
			}

			// 12. The Explore grid doesn't scroll and sizes each tile as rows * 0.055 of its cell, which is 1.45 screens:
			//     that fits four rows. With the extra tiles (five rows), Extras makes the cell taller and the factor smaller.
			MethodDef getView = Program.FindMethod(Program.FindType(chewie, "chewie.Droid.Features.Home.HomeListViewAdapter"), "GetView");
			{
				IList<Instruction> instructions7 = getView.Body.Instructions;
				List<Instruction> heights = instructions7.Where((Instruction x) => x.OpCode == OpCodes.Ldfld && ((IField)x.Operand).Name == "Height").ToList();
				Program.Expect(heights.Count == 1, "expected one HomeCellData.Height read in " + getView.FullName);
				instructions7.Insert(instructions7.IndexOf(heights[0]) + 1, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "HomeCellHeight"))));
				Console.WriteLine("Patched " + getView.FullName);
			}
			MethodDef createHolder = Program.FindMethod(Program.FindType(chewie, "chewie.Droid.Features.Home.ExploreRecyclerAdapter"), "OnCreateViewHolder");
			Instruction factor = createHolder.Body.Instructions.Single((Instruction x) => x.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)x.Operand - 0.055f) < 1e-6f);
			factor.OpCode = OpCodes.Call;
			factor.Operand = importer.Import(Program.FindMethod(extras, "ExploreTileFactor"));
			Console.WriteLine("Patched " + createHolder.FullName);

			// 13. RobotSettingsService.SetEyeColor: picking a preset also switches off the custom eye color.
			MethodDef setEyeColor = Program.FindMethod(Program.FindType(chewie, "chewie.Features.Settings.RobotSettingsService"), "SetEyeColor");
			setEyeColor.Body.Instructions.Insert(0, OpCodes.Ldarg_1.ToInstruction());
			setEyeColor.Body.Instructions.Insert(1, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "PresetEyeColorPicked"))));
			Console.WriteLine("Patched " + setEyeColor.FullName);

			// 14. MainSettingsViewModel.NetworkIndex is a fixed row position (1 or 2); the eye color row above it adds one.
			MethodDef networkIndex = Program.FindMethod(Program.FindType(chewie, "chewie.Settings.MainSettingsViewModel"), "get_NetworkIndex");
			Program.HookReturnValue(networkIndex, importer.Import(Program.FindMethod(extras, "ShiftSettingsIndex")));
			Console.WriteLine("Patched " + networkIndex.FullName);

			// 14b. Fix for when robot's eye color isn't in the defined eye color list
			{
				TypeDef mainSettingsVm = Program.FindType(chewie, "chewie.Settings.MainSettingsViewModel");
				MethodDef getEyeColorName = Program.FindMethod(mainSettingsVm, "GetEyeColorDisplayName");
				FieldDef dataField = mainSettingsVm.Fields.Single((FieldDef f) => f.Name == "_MainSettingsData");
				FieldDef eyeColorsField = Program.FindType(chewie, "chewie.Settings.MainSettingsData").Fields.Single((FieldDef f) => f.Name == "EyeColorCollectionObjs");
				IMethod listGetItem = chewie.GetTypes().SelectMany((TypeDef t) => t.Methods).Where((MethodDef m) => m.HasBody)
					.SelectMany((MethodDef m) => m.Body.Instructions)
					.Where((Instruction x) => x.OpCode == OpCodes.Callvirt && x.Operand is IMethod im && im.Name == "get_Item" && im.DeclaringType != null && im.DeclaringType.FullName.Contains("EyeColorCollectionData"))
					.Select((Instruction x) => (IMethod)x.Operand).First();
				IList<Instruction> il14 = getEyeColorName.Body.Instructions;
				Program.Expect(il14.Count(x => x.OpCode == OpCodes.Ret) == 1 && il14[il14.Count - 1].OpCode == OpCodes.Ret, "unexpected IL in " + getEyeColorName.FullName);
				Instruction ret14 = il14[il14.Count - 1];
				int at14 = il14.Count - 1;
				il14.Insert(at14++, OpCodes.Dup.ToInstruction());
				il14.Insert(at14++, OpCodes.Brtrue.ToInstruction(ret14));
				il14.Insert(at14++, OpCodes.Pop.ToInstruction());
				il14.Insert(at14++, OpCodes.Ldarg_0.ToInstruction());
				il14.Insert(at14++, OpCodes.Ldfld.ToInstruction(dataField));
				il14.Insert(at14++, OpCodes.Ldfld.ToInstruction(eyeColorsField));
				il14.Insert(at14++, OpCodes.Ldc_I4_0.ToInstruction());
				il14.Insert(at14++, OpCodes.Callvirt.ToInstruction(listGetItem));
				getEyeColorName.Body.SimplifyBranches();
				getEyeColorName.Body.OptimizeBranches();
				Console.WriteLine("Patched " + getEyeColorName.FullName);
			}

			// 15. MvvmCross's MvxLayoutInflater.CreateCustomViewInternal swaps the context through the hidden field
			//     LayoutInflater.mConstructorArgs, which Android 10+ blocks for apps targeting it (crash on the first screen).
			//     Before that block: on Android 10+, use the public createView(Context, ...) instead and skip the block.
			ModuleDefMD mvxBinding = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(refDir, "MvvmCross.Binding.Droid.dll")), moduleContext);
			{
				MethodDef createCustom = Program.FindMethod(Program.FindType(mvxBinding, "MvvmCross.Binding.Droid.Views.MvxLayoutInflater"), "CreateCustomViewInternal");
				IList<Instruction> il = createCustom.Body.Instructions;
				Instruction block = il.Single((Instruction x) => x.OpCode == OpCodes.Ldfld && ((IField)x.Operand).Name == "_constructorArgs"
					&& il.IndexOf(x) > 0 && il[il.IndexOf(x) - 1].OpCode == OpCodes.Ldarg_0 && il[il.IndexOf(x) + 1].OpCode == OpCodes.Brtrue_S);
				Instruction start = il[il.IndexOf(block) - 1];
				Instruction end = il[il.Count - 2];
				Program.Expect(il[il.Count - 1].OpCode == OpCodes.Ret && end.OpCode == OpCodes.Ldarg_2, createCustom.FullName + " doesn't end with return view");
				Importer mvxImporter = new Importer(mvxBinding, ImporterOptions.TryToUseDefs);
				TypeDef inflater = Program.FindType(wirePod, "WirePod.Inflater");
				int at = il.IndexOf(start);
				Instruction[] added =
				{
					OpCodes.Call.ToInstruction(mvxImporter.Import(Program.FindMethod(inflater, "UseCreateViewWithContext"))),
					OpCodes.Brfalse.ToInstruction(start),
					OpCodes.Ldarg_0.ToInstruction(),
					OpCodes.Ldarg_3.ToInstruction(),
					OpCodes.Ldarg_S.ToInstruction(createCustom.Parameters[4]),
					OpCodes.Ldarg_S.ToInstruction(createCustom.Parameters[5]),
					OpCodes.Call.ToInstruction(mvxImporter.Import(Program.FindMethod(inflater, "CreateView"))),
					OpCodes.Starg_S.ToInstruction(createCustom.Parameters[2]),
					OpCodes.Br.ToInstruction(end)
				};
				for (int k = 0; k < added.Length; k++)
				{
					il.Insert(at + k, added[k]);
				}
				// The branch that fell through into the block (view == null) now falls into the new code first.
				Console.WriteLine("Patched " + createCustom.FullName);
			}

			// 16. Settings > About: "Check for updates" (UpdateActivity) as the first row.
			Program.EditJsonResource(chewieResources, "ChewieResources.Settings.AboutSettingsData.json", delegate(JsonNode node)
			{
				node["AboutSettingsTableObjs"].AsArray().Insert(0, new JsonObject
				{
					["CellType"] = 0, ["Title"] = "Check for updates", ["NavigationKey"] = "wirepod_update", ["Height"] = 0.1
				});
			});
			MethodDef aboutRow = Program.FindMethod(Program.FindType(chewie, "chewie.Features.Settings.AboutSettingsViewModel"), "OnRowSelected");
			{
				IList<Instruction> il16 = aboutRow.Body.Instructions;
				Instruction first16 = il16[0];
				il16.Insert(0, OpCodes.Ldarg_1.ToInstruction());
				il16.Insert(1, OpCodes.Call.ToInstruction(importer.Import(Program.FindMethod(extras, "OpenAboutRow"))));
				il16.Insert(2, OpCodes.Brfalse.ToInstruction(first16));
				il16.Insert(3, OpCodes.Ret.ToInstruction());
				Console.WriteLine("Patched " + aboutRow.FullName);
			}

			// 17. The cloud session request (the "Securing WiFi" step) sends this phone's name as clientName, read from
			// Settings.Secure "bluetooth_name". Apps targeting API 32+ get a SecurityException for that key, so the request
			// was never sent and the app reported "no wifi on robot" after a 30 s switchboard timeout. Use Build.MODEL.
			ModuleDefMD core = ModuleDefMD.Load(File.ReadAllBytes(Path.Combine(refDir, "core.Droid.dll")), moduleContext);
			{
				MethodDef deviceName = Program.FindMethod(Program.FindType(core, "core.Droid.Services.AppInfo.NativeAppInfoService"), "GetDeviceName");
				Program.Expect(deviceName.Body.Instructions.Any((Instruction i) => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "bluetooth_name"),
					deviceName.FullName + ": bluetooth_name lookup not found");
				ModuleDef monoAndroid = assemblyResolver.Resolve(new AssemblyNameInfo("Mono.Android"), core).ManifestModule;
				IMethod getModel = new Importer(core, ImporterOptions.TryToUseDefs).Import(
					Program.FindMethod(Program.FindType(monoAndroid, "Android.OS.Build"), "get_Model"));
				deviceName.Body = new CilBody();
				deviceName.Body.Instructions.Add(OpCodes.Call.ToInstruction(getModel));
				deviceName.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
				Console.WriteLine("Patched " + deviceName.FullName);
			}

			// 18. Dead Anki links. About: drop the Anki legal/contact rows. Hardcoded URLs in code: point them at
			// DDL's help center and shop. (Links in Android string resources are rewritten by the build script.)
			string[] deadAboutRows = { "About.acknowledgmentsURL", "About.termsURL", "About.policyURL", "About.contactURL" };
			Program.EditJsonResource(chewieResources, "ChewieResources.Settings.AboutSettingsData.json", delegate(JsonNode node)
			{
				JsonArray rows = node["AboutSettingsTableObjs"].AsArray();
				int removed = 0;
				for (int i = rows.Count - 1; i >= 0; i--)
				{
					if (Array.IndexOf(deadAboutRows, (string)rows[i]["NavigationKey"]) >= 0)
					{
						rows.RemoveAt(i);
						removed++;
					}
				}
				Program.Expect(removed == deadAboutRows.Length, "About: expected " + deadAboutRows.Length + " Anki rows, removed " + removed);
			});
			Dictionary<string, string> urlFixes = new Dictionary<string, string>
			{
				["https://support.anki.com/"] = "https://support.anki.bot/article/104-troubleshooting-vectore28099s-connection",
				["https://www.anki.com/en-us/vector"] = "https://anki.bot/products/vector-robot",
				["https://play.google.com/store/apps/dev?id=8565220581183083218"] = "https://anki.bot/products/vector-robot",
			};
			Dictionary<string, int> urlHits = urlFixes.Keys.ToDictionary((string k) => k, (string k) => 0);
			foreach (TypeDef type17 in chewie.GetTypes())
			{
				foreach (MethodDef method17 in type17.Methods.Where((MethodDef m) => m.HasBody))
				{
					foreach (Instruction ins in method17.Body.Instructions)
					{
						string s17 = ins.Operand as string;
						if (ins.OpCode == OpCodes.Ldstr && s17 != null && urlFixes.ContainsKey(s17))
						{
							urlHits[s17]++;
							ins.Operand = urlFixes[s17];
							Console.WriteLine("Patched URL in " + method17.FullName + ": " + s17 + " -> " + urlFixes[s17]);
						}
					}
				}
			}
			foreach (KeyValuePair<string, int> hit in urlHits)
			{
				Program.Expect(hit.Value == 1, "URL " + hit.Key + ": expected 1 site, found " + hit.Value);
			}

			// 19. A reconnect that fails while the app is in the background (Android blocks its network there) is held by
			// WirePod.Reconnect and retried once the app is in front, instead of parking on "can't connect to Vector".
			{
				TypeDef connModule = Program.FindType(chewie, "chewie.Services.RobotCommunicationConnectionModule");
				MethodDef reconnectFinished = Program.FindMethod(connModule, "HandleAttemptReconnectToGeneralNetworkFinished");
				FieldDef reconnectingFlag = connModule.Fields.Single((FieldDef f) => f.Name == "_IsReconnectingToGeneralNetwork");
				TypeDef reconnectType = Program.FindType(wirePod, "WirePod.Reconnect");
				Program.FindMethod(reconnectType, "DeferFailure");
				// Like Attach above: build the reference by hand so the signature uses chewie's own TypeDef rather than
				// a TypeRef to chewie through WirePod.dll (which Mono can't load).
				MemberRef deferFailure = new MemberRefUser(chewie, "DeferFailure",
					MethodSig.CreateStatic(chewie.CorLibTypes.Boolean, new ClassSig(connModule), chewie.CorLibTypes.Boolean, chewie.CorLibTypes.Boolean),
					importer.Import(reconnectType));
				IList<Instruction> il19 = reconnectFinished.Body.Instructions;
				Instruction first19 = il19[0];
				Instruction[] hook19 =
				{
					OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldarg_1.ToInstruction(), OpCodes.Ldarg_2.ToInstruction(),
					OpCodes.Call.ToInstruction(deferFailure), OpCodes.Brfalse.ToInstruction(first19),
					OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldc_I4_0.ToInstruction(), OpCodes.Stfld.ToInstruction(reconnectingFlag),
					OpCodes.Ret.ToInstruction(),
				};
				for (int i = 0; i < hook19.Length; i++)
				{
					il19.Insert(i, hook19[i]);
				}
				reconnectFinished.Body.MaxStack = Math.Max(reconnectFinished.Body.MaxStack, (ushort)3);
				Console.WriteLine("Patched " + reconnectFinished.FullName);
			}

			Directory.CreateDirectory(outDir);
			core.Write(Path.Combine(outDir, "core.Droid.dll"), Program.PreservingWriterOptions(core));
			// Keep metadata tokens stable; nothing else in the app has to change.
			chewie.Write(Path.Combine(outDir, "chewie.Droid.dll"), Program.PreservingWriterOptions(chewie));
			chewieResources.Write(Path.Combine(outDir, "ChewieResources.dll"), Program.PreservingWriterOptions(chewieResources));
			mvxBinding.Write(Path.Combine(outDir, "MvvmCross.Binding.Droid.dll"), Program.PreservingWriterOptions(mvxBinding));
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
		private static void EditJsonResource(ModuleDef module, string name, Action<JsonNode> edit)
		{
			int num = module.Resources.ToList().FindIndex((Resource r) => r.Name == name);
			Program.Expect(num >= 0, "resource not found: " + name);
			EmbeddedResource embeddedResource = (EmbeddedResource)module.Resources[num];
			string text = Encoding.UTF8.GetString(embeddedResource.CreateReader().ToArray()).TrimStart('﻿');
			// Some of the app's JSON files start with // comments; Newtonsoft (which the app uses) accepts them.
			JsonNode jsonObject = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
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
			foreach (string text in new string[] { "WirePod.dll", "chewie.Droid.dll", "core.Droid.dll" })
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
			Dictionary<string, byte[]> dictionary = Program.ExpandEntries(entries).ToDictionary((KeyValuePair<string, string> e) => e.Key, (KeyValuePair<string, string> e) => File.ReadAllBytes(e.Value));
			ApkZip.Rewrite(apk, outApk, dictionary, new Func<string, bool>(ApkZip.IsV1SignatureFile));
			Console.WriteLine("Packed " + outApk);
		}

		/// <summary>"entry=file" pairs, and "@dir" for every file under dir (entry = its path relative to dir).</summary>
		private static Dictionary<string, string> ExpandEntries(IEnumerable<string> entries)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			foreach (string text in entries)
			{
				if (text.StartsWith("@"))
				{
					string root = Path.GetFullPath(text.Substring(1));
					foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
					{
						dictionary[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = file;
					}
					continue;
				}
				int num = text.IndexOf('=');
				dictionary[text.Substring(0, num)] = text.Substring(num + 1);
			}
			return dictionary;
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
