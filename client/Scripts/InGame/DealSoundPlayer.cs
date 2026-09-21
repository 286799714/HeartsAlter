using System;
using Godot;

namespace HeartsAlter.Scripts.InGame;

/// <summary>
/// Mixes a deal sequence on the audio sample clock. Card requests only start
/// and end the sequence; neither their timing nor voice completion gates beats.
/// </summary>
public partial class DealSoundPlayer : AudioStreamPlayer
{
	private Vector2[] _sample = Array.Empty<Vector2>();
	private Vector2[] _buffer = Array.Empty<Vector2>();
	private AudioStreamGeneratorPlayback _playback;
	private int _mixRate;
	private int _intervalFrames;
	private long _writtenFrames;
	private long _lastOnsetFrame;
	private bool _emitting;

	public override void _Ready()
	{
		SetProcess(false);
		if (Stream is null) return;
		_mixRate = (int)AudioServer.GetMixRate();
		using AudioStreamPlayback source = Stream.InstantiatePlayback();
		source.Start();
		_sample = source.MixAudio(1.0f, (int)Math.Ceiling(Stream.GetLength() * _mixRate));
		source.Stop();
		Stream = new AudioStreamGenerator
		{
			MixRateMode = AudioStreamGenerator.AudioStreamGeneratorMixRate.Custom,
			MixRate = _mixRate,
			BufferLength = 0.1f
		};
	}

	public void BeginSequence(float interval)
	{
		if (_playback is not null || _sample.Length == 0) return;
		// Choose a sustainable period once; never wait for individual voices.
		int minimumFrames = (int)Math.Ceiling((double)_sample.Length / Math.Max(1, MaxPolyphony));
		_intervalFrames = Math.Max(minimumFrames, (int)Math.Round(interval * _mixRate));
		_writtenFrames = 0;
		_lastOnsetFrame = -1;
		_emitting = true;
		Play();
		_playback = (AudioStreamGeneratorPlayback)GetStreamPlayback();
		_buffer = new Vector2[_playback.GetFramesAvailable()];
		FillBuffer();
		SetProcess(true);
	}

	/// <summary>Let all already scheduled sounds finish their complete tails.</summary>
	public void EndSequence() => _emitting = false;

	public void ResetSequence()
	{
		_emitting = false;
		Stop();
		_playback = null;
		SetProcess(false);
	}

	public override void _ExitTree() => ResetSequence();

	public override void _Process(double delta)
	{
		if (_playback is null) return;
		int queuedFrames = _buffer.Length - _playback.GetFramesAvailable();
		long heardFrames = _writtenFrames - queuedFrames;
		if (!_emitting && heardFrames >= _lastOnsetFrame + _sample.Length)
		{
			ResetSequence();
			return;
		}
		FillBuffer();
	}

	private void FillBuffer()
	{
		int count = _playback.GetFramesAvailable();
		for (int index = 0; index < count; index++, _writtenFrames++)
		{
			if (_emitting && _writtenFrames % _intervalFrames == 0)
				_lastOnsetFrame = _writtenFrames;
			Vector2 mixed = Vector2.Zero;
			for (long onset = _lastOnsetFrame;
				onset >= 0 && _writtenFrames - onset < _sample.Length;
				onset -= _intervalFrames)
			{
				mixed += _sample[(int)(_writtenFrames - onset)];
			}
			_buffer[index] = mixed;
		}
		if (count > 0)
			_playback.PushBuffer(_buffer.AsSpan(0, count));
	}
}
