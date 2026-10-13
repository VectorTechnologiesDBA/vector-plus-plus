using System;
using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Views;

namespace WirePod
{
	/// <summary>
	/// MvvmCross's MvxLayoutInflater creates custom views by swapping the context in LayoutInflater.mConstructorArgs, a
	/// hidden field that Android 10+ blocks for apps targeting it (this build targets Android 15). Android 10 added a
	/// public LayoutInflater.createView(Context, name, prefix, attrs) for exactly this, which newer MvvmCross uses; the
	/// patched CreateCustomViewInternal calls this instead on Android 10+.
	/// </summary>
	public static class Inflater
	{
		public static bool UseCreateViewWithContext()
		{
			return Inflater.SdkInt() >= 29;
		}

		/// <summary>The view, or null when name isn't a class (as the original code's ClassNotFoundException case).</summary>
		public static View CreateView(LayoutInflater inflater, string name, Context viewContext, IAttributeSet attrs)
		{
			IntPtr jname = JNIEnv.NewString(name);
			try
			{
				IntPtr method = JNIEnv.GetMethodID(JNIEnv.FindClass("android/view/LayoutInflater"), "createView",
					"(Landroid/content/Context;Ljava/lang/String;Ljava/lang/String;Landroid/util/AttributeSet;)Landroid/view/View;");
				IntPtr view = JNIEnv.CallObjectMethod(inflater.Handle, method, new JValue[]
				{
					new JValue(viewContext), new JValue(jname), new JValue(IntPtr.Zero), new JValue(attrs)
				});
				return Java.Lang.Object.GetObject<View>(view, JniHandleOwnership.TransferLocalRef);
			}
			catch (Java.Lang.ClassNotFoundException)
			{
				return null;
			}
			finally
			{
				JNIEnv.DeleteLocalRef(jname);
			}
		}

		private static int SdkInt()
		{
			IntPtr version = JNIEnv.FindClass("android/os/Build$VERSION");
			return JNIEnv.GetStaticIntField(version, JNIEnv.GetStaticFieldID(version, "SDK_INT", "I"));
		}
	}
}
