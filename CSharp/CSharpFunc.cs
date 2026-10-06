using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using RunOnStartup;
using System.IO;

public partial class CSharpFunc : Node
{
	#region Image

	public static Godot.Collections.Array<Color> ExtractThemeColors(
		Image image,
		int colorCount = 6,
		bool applySaliency = true,
		float minColorDistance = 0.15f)
	{
		var pixels = SamplePixels(image, maxSamples: 2000);
		if (pixels.Count == 0)
			return [Colors.Black];

		var random = new Random();
		var initialCentroids = InitializeCentroidsKMeansPlusPlus(pixels, colorCount, random);

		var (centroids, assignments) = KMeansCluster(pixels, initialCentroids, maxIterations: 15);

		float[] weights = new float[centroids.Count];
		for (int i = 0; i < assignments.Length; i++)
			weights[assignments[i]]++;

		if (applySaliency)
		{
			float avgSaturation = 0, avgValue = 0;
			foreach (var pixel in pixels)
			{
				pixel.ToHsv(out float _, out float s, out float v);
				avgSaturation += s;
				avgValue += v;
			}
			avgSaturation /= pixels.Count;
			avgValue /= pixels.Count;

			float[] saliencyFactors = new float[centroids.Count];
			for (int i = 0; i < centroids.Count; i++)
			{
				centroids[i].ToHsv(out float _, out float s, out float v);
				float satDiff = Mathf.Abs(s - avgSaturation);
				float valDiff = Mathf.Abs(v - avgValue);
				float factor = 1.0f + satDiff * 1.5f + valDiff * 1.2f + s * 2.0f;
				saliencyFactors[i] = factor;
			}

			for (int i = 0; i < weights.Length; i++)
				weights[i] *= saliencyFactors[i];
		}

		var finalColors = SelectDiverseColors(centroids, weights, colorCount, minColorDistance);

		return [.. finalColors];
	}

	private static List<Color> SamplePixels(Image image, int maxSamples)
	{
		var pixels = new List<Color>(maxSamples);
		image.GetData();
		int width = image.GetWidth();
		int height = image.GetHeight();

		if (width * height <= maxSamples)
		{
			for (int x = 0; x < width; x++)
				for (int y = 0; y < height; y++)
				{
					Color c = image.GetPixel(x, y);
					if (c.A > 0.1f) pixels.Add(c);
				}
		}
		else
		{
			float step = Mathf.Sqrt(width * height / (float)maxSamples);
			for (float fx = 0; fx < width; fx += step)
				for (float fy = 0; fy < height; fy += step)
				{
					int x = (int)fx;
					if (x >= width) x = width - 1;
					int y = (int)fy;
					if (y >= height) y = height - 1;
					Color c = image.GetPixel(x, y);
					if (c.A > 0.1f) pixels.Add(c);
				}
		}
		return pixels;
	}

	private static List<Color> InitializeCentroidsKMeansPlusPlus(List<Color> pixels, int k, Random random)
	{
		var centroids = new List<Color>
		{
			// 随机选第一个中心
			pixels[random.Next(pixels.Count)]
		};

		for (int i = 1; i < k; i++)
		{
			float[] minDistSquared = new float[pixels.Count];
			float totalDist = 0f;
			for (int j = 0; j < pixels.Count; j++)
			{
				float minDist = float.MaxValue;
				foreach (var c in centroids)
				{
					float dr = pixels[j].R - c.R;
					float dg = pixels[j].G - c.G;
					float db = pixels[j].B - c.B;
					float distSq = dr * dr + dg * dg + db * db;
					if (distSq < minDist) minDist = distSq;
				}
				minDistSquared[j] = minDist;
				totalDist += minDist;
			}

			// 轮盘赌选下一个中心
			float r = (float)random.NextDouble() * totalDist;
			float cumulative = 0f;
			int selectedIdx = 0;
			for (int j = 0; j < pixels.Count; j++)
			{
				cumulative += minDistSquared[j];
				if (cumulative >= r)
				{
					selectedIdx = j;
					break;
				}
			}
			centroids.Add(pixels[selectedIdx]);
		}

		return centroids;
	}

	private static (List<Color> centroids, int[] assignments) KMeansCluster(
		List<Color> pixels,
		List<Color> initialCentroids,
		int maxIterations)
	{
		int k = initialCentroids.Count;
		var centroids = new List<Color>(initialCentroids);
		int[] assignments = new int[pixels.Count];
		bool changed;

		for (int iter = 0; iter < maxIterations; iter++)
		{
			changed = false;

			for (int i = 0; i < pixels.Count; i++)
			{
				int best = FindNearestCentroid(pixels[i], centroids);
				if (assignments[i] != best)
				{
					assignments[i] = best;
					changed = true;
				}
			}

			if (!changed) break;

			var newCentroids = new List<Color>();
			for (int j = 0; j < k; j++)
			{
				Vector3 sum = Vector3.Zero;
				int count = 0;
				for (int i = 0; i < pixels.Count; i++)
				{
					if (assignments[i] == j)
					{
						sum += new Vector3(pixels[i].R, pixels[i].G, pixels[i].B);
						count++;
					}
				}
				if (count > 0)
					newCentroids.Add(new Color(sum.X / count, sum.Y / count, sum.Z / count));
				else
					newCentroids.Add(centroids[j]);
			}
			centroids = newCentroids;
		}

		return (centroids, assignments);
	}

	private static int FindNearestCentroid(Color pixel, List<Color> centroids)
	{
		int bestIdx = 0;
		float bestDistSq = float.MaxValue;
		for (int i = 0; i < centroids.Count; i++)
		{
			float dr = pixel.R - centroids[i].R;
			float dg = pixel.G - centroids[i].G;
			float db = pixel.B - centroids[i].B;
			float distSq = dr * dr + dg * dg + db * db;
			if (distSq < bestDistSq)
			{
				bestDistSq = distSq;
				bestIdx = i;
			}
		}
		return bestIdx;
	}

	private static List<Color> SelectDiverseColors(
		List<Color> candidates,
		float[] weights,
		int desiredCount,
		float minDistance)
	{
		var indexed = candidates
			.Select((c, i) => new { Color = c, Weight = weights[i] })
			.OrderByDescending(x => x.Weight)
			.ToList();

		var result = new List<Color>();

		foreach (var item in indexed)
		{
			bool tooClose = false;
			foreach (var selected in result)
			{
				float dr = item.Color.R - selected.R;
				float dg = item.Color.G - selected.G;
				float db = item.Color.B - selected.B;
				float dist = Mathf.Sqrt(dr * dr + dg * dg + db * db);
				if (dist < minDistance)
				{
					tooClose = true;
					break;
				}
			}
			if (!tooClose)
			{
				result.Add(item.Color);
				if (result.Count >= desiredCount)
					break;
			}
		}

		// 如果颜色不够，直接按权重补齐
		if (result.Count < desiredCount)
		{
			var remaining = indexed.Where(x => !result.Contains(x.Color)).ToList();
			foreach (var item in remaining)
			{
				result.Add(item.Color);
				if (result.Count >= desiredCount)
					break;
			}
		}

		return result;
	}

	public static string NormalizePathSimple(string path, bool isFile = false)
	{
		if (string.IsNullOrEmpty(path))
			return path;

		if (path.StartsWith('*'))
		{
			path = $"user://List/{path[1..]}/";
		}
		else
		{
			path = path.Replace('\\', '/');
			if (!path.EndsWith('/') && !isFile)
				path += "/";
			if (path.Length >= 2 && path[1] == ':' && path[2] != '/')
				path = path.Insert(2, "/");
		}

		return ProjectSettings.GlobalizePath(path);
	}

	#endregion

	#region Startup

	const string UNIQUE_NAME = "io.github.by-chi.BiliMusic";

	/// <summary>
	/// 设置开机启动（仅支持 Windows）
	/// </summary>
	public static void SetRunOnStartup(bool value)
	{
#if DEBUG
		return;
#endif

		if (!OperatingSystem.IsWindows())
		{
			GD.PrintErr("[SetRunOnStartup] 开机启动功能仅在 Windows 平台上支持");
			return;
		}

		try
		{
			if (value)
			{
				string executablePath = System.Environment.ProcessPath;
				RunOnStartupManager.Instance.Register(UNIQUE_NAME, executablePath, allUsers: false);
				GD.Print("[SetRunOnStartup] 已注册开机启动");
			}
			else
			{
				RunOnStartupManager.Instance.Unregister(UNIQUE_NAME, allUsers: false);
				GD.Print("[SetRunOnStartup] 已取消开机启动");
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[SetRunOnStartup] 设置开机启动失败: {ex.Message}");
		}
	}

	/// <summary>
	/// 检查是否已启用开机启动（仅支持 Windows）
	/// </summary>
	public static bool IsRunOnStartupEnabled()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			return RunOnStartupManager.Instance.IsRegistered(UNIQUE_NAME, allUsers: false);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"[IsRunOnStartupEnabled] 检查开机启动状态失败: {ex.Message}");
			return false;
		}
	}

	#endregion

	public static float GetDirectorySize(string path)
	{
		if (!Directory.Exists(path))
		{
			GD.PrintErr($"目录不存在: {path}");
			return -1;
		}

		long totalBytes = 0;
		var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);

		foreach (var file in files)
		{
			totalBytes += new FileInfo(file).Length;
		}
		double sizeInMB = totalBytes / (1024.0 * 1024.0);
		return (float)Math.Round(sizeInMB, 2);
	}

	/// <summary>
	/// 从标题提取歌名
	/// </summary>
	public static string ExtractSongName(string title)
	{
		return SongInfoExtractor.ExtractSongName(title);
	}

	/// <summary>
	/// 从标题提取歌名和歌手（返回 Dictionary，keys: "song", "singer"）
	/// </summary>
	public static Godot.Collections.Dictionary<string, string> ExtractFeatures(string title)
	{
		return SongInfoExtractor.ExtractFeatures(title);
	}

	/// <summary>
	/// 批量处理多个标题
	/// </summary>
	public static Godot.Collections.Array<Godot.Collections.Dictionary<string, string>> ExtractBatch(Godot.Collections.Array<string> titles)
	{
		var results = new Godot.Collections.Array<Godot.Collections.Dictionary<string, string>>();
		foreach (var t in titles)
			results.Add(ExtractFeatures(t));
		return results;
	}
}
