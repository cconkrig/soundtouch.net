﻿// License :
//
// SoundTouch audio processing library
// Copyright (c) Olli Parviainen
// C# port Copyright (c) Olaf Woudenberg
//
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307  USA

namespace SoundTouch
{
    using System;
    using System.Diagnostics;

    using SoundTouch.Assets;
    using JetBrains.Annotations;

    internal class TimeStretch : FifoProcessor
    {
        private readonly FifoSampleBuffer _outputBuffer;
        private readonly FifoSampleBuffer _inputBuffer;

        private int _channels;
        private int _sampleReq;

        private int _overlapLength;
        private int _seekLength;
        private int _seekWindowLength;
        private int _overlapDividerBitsNorm;
        private int _sampleRate;
        private int _sequenceMs;
        private int _seekWindowMs;
        private int _overlapMs;

        private long _maxnorm;
        private float _maxnormf;

        private double _tempo;
        private double _nominalSkip;
        private double _skipFract;

        private bool _bQuickSeek;
        private bool _bAutoSeqSetting;
        private bool _bAutoSeekSetting;
        private bool _isBeginning;

        private float[] _pMidBuffer;

        public TimeStretch()
            : this(new FifoSampleBuffer())
        {
        }

        private TimeStretch(FifoSampleBuffer outputBuffer)
            : base(outputBuffer)
        {
            _outputBuffer = outputBuffer;
            _inputBuffer = new FifoSampleBuffer();

            _bQuickSeek = false;
            _channels = 2;

#if NET45 || NETSTANDARD1_1
            _pMidBuffer = new float[0];
#else
            _pMidBuffer = Array.Empty<float>();
#endif
            _overlapLength = 0;

            _bAutoSeqSetting = true;
            _bAutoSeekSetting = true;

            _tempo = 1.0f;
            SetParameters(44100, Defaults.SEQUENCE_MS, Defaults.SEEKWINDOW_MS, Defaults.OVERLAP_MS);
            SetTempo(1.0f);

            Clear();
        }

        /// <summary>
        /// Returns the output buffer object.
        /// </summary>
        public FifoSamplePipe GetOutput() => _outputBuffer;

        /// <summary>
        /// Returns the input buffer object.
        /// </summary>
        public FifoSamplePipe GetInput() => _inputBuffer;

        /// <summary>
        /// Sets new target tempo. Normal tempo = 'SCALE', smaller values represent slower
        /// tempo, larger faster tempo.
        /// </summary>
        public void SetTempo(double newTempo)
        {
            int intskip;

            _tempo = newTempo;

            // Calculate new sequence duration
            CalcSeqParameters();

            // Calculate ideal skip length (according to tempo value)
            _nominalSkip = _tempo * (_seekWindowLength - _overlapLength);
            intskip = (int)(_nominalSkip + 0.5);

            // Calculate how many samples are needed in the 'inputBuffer' to
            // process another batch of samples
            _sampleReq = Math.Max(intskip + _overlapLength, _seekWindowLength) + _seekLength;
        }

        /// <inheritdoc />
        public override void Clear()
        {
            _outputBuffer.Clear();
            ClearInput();
        }

        /// <summary>
        /// Clears the input buffer.
        /// </summary>
        public void ClearInput()
        {
            _inputBuffer.Clear();
            ClearMidBuffer();
            _isBeginning = true;

            _maxnorm = 0;
            _maxnormf = 1e8f;

            _skipFract = 0;
        }

        /// <summary>
        /// Sets the number of channels, 1 = mono, 2 = stereo.
        /// </summary>
        public void SetChannels(int numChannels)
        {
            if (!VerifyNumberOfChannels(numChannels) || (_channels == numChannels))
                return;

            _channels = numChannels;
            _inputBuffer.Channels = _channels;
            _outputBuffer.Channels = _channels;

            // re-init overlap/buffer
            _overlapLength = 0;
            SetParameters(_sampleRate);
        }

        /// <summary>
        /// Enables/disables the quick position seeking algorithm. <see langword="true"/> to disable,
        /// <see langword="false"/> to enable.
        /// </summary>
        public void EnableQuickSeek(bool enable) => _bQuickSeek = enable;

        /// <summary>
        /// Gets a value indicating whether the quick seeking algorithm is enabled.
        /// </summary>
        [Pure]
        public bool IsQuickSeekEnabled() => _bQuickSeek;

        /// <summary>
        /// Sets routing control parameters. These controls are certain time constants
        /// defining how the sound is stretched to the desired duration.
        /// </summary>
        /// <param name="sampleRate">Sample-rate of the sound being processed (Hz).</param>
        /// <param name="sequenceMS">Single processing sequence length (ms).</param>
        /// <param name="seekwindowMS">Seeking window length for scanning the best overlapping position (ms).</param>
        /// <param name="overlapMS">Sequence overlapping length (ms).</param>
        public void SetParameters(int sampleRate, int sequenceMS = -1, int seekwindowMS = -1, int overlapMS = -1)
        {
            // accept only positive parameter values - if zero or negative, use old values instead
            if (sampleRate > 0)
            {
                if (sampleRate > 192000)
                    throw new ArgumentException(Strings.Argument_ExcessiveSampleRate);

                _sampleRate = sampleRate;
            }

            if (overlapMS > 0)
                _overlapMs = overlapMS;

            if (sequenceMS > 0)
            {
                _sequenceMs = sequenceMS;
                _bAutoSeqSetting = false;
            }
            else if (sequenceMS == 0)
            {
                // if zero, use automatic setting
                _bAutoSeqSetting = true;
            }

            if (seekwindowMS > 0)
            {
                _seekWindowMs = seekwindowMS;
                _bAutoSeekSetting = false;
            }
            else if (seekwindowMS == 0)
            {
                // if zero, use automatic setting
                _bAutoSeekSetting = true;
            }

            CalcSeqParameters();

            CalculateOverlapLength(_overlapMs);

            // set tempo to recalculate 'sampleReq'
            SetTempo(_tempo);
        }

        /// <summary>
        /// Get routine control parameters, see setParameters() function.
        /// Any of the parameters to this function can be <see langword="null"/>, in such case corresponding parameter
        /// value isn't returned.
        /// </summary>
        [Pure]
        public void GetParameters(out int pSampleRate, out int pSequenceMs, out int pSeekWindowMs, out int pOverlapMs)
        {
            pSampleRate = _sampleRate;

            pSequenceMs = _bAutoSeqSetting ? Defaults.USE_AUTO_SEQUENCE_LEN : _sequenceMs;

            pSeekWindowMs = _bAutoSeekSetting ? Defaults.USE_AUTO_SEEKWINDOW_LEN : _seekWindowMs;

            pOverlapMs = _overlapMs;
        }

        /// <summary>
        /// Adds 'numsamples' pcs of samples from the 'samples' memory position into
        /// the input of the object.
        /// </summary>
        public override void PutSamples(in ReadOnlySpan<float> samples, int numSamples)
        {
            // Add the samples into the input buffer
            _inputBuffer.PutSamples(samples, numSamples);

            // Process the samples in input buffer
            ProcessSamples();
        }

        /// <summary>
        /// return nominal input sample requirement for triggering a processing batch.
        /// </summary>
        [Pure]
        public int GetInputSampleReq() => (int)(_nominalSkip + 0.5);

        /// <summary>
        /// return nominal output sample amount when running a processing batch.
        /// </summary>
        [Pure]
        public int GetOutputBatchSize() => _seekWindowLength - _overlapLength;

        /// <summary>
        /// return approximate initial input-output latency.
        /// </summary>
        [Pure]
        public int GetLatency() => _sampleReq;

        private static void ClearCrossCorrState()
        {
            // default implementation is empty.
        }

        private void AcceptNewOverlapLength(int newOverlapLength)
        {
            if (newOverlapLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(newOverlapLength));

            int prevOvl = _overlapLength;
            _overlapLength = newOverlapLength;

            if (_overlapLength > prevOvl)
            {
                _pMidBuffer = new float[_overlapLength * _channels];

                ClearMidBuffer();
            }
        }

        private void CalculateOverlapLength(int overlapMs)
        {
            if (overlapMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(overlapMs));

            int newOvl = (_sampleRate * overlapMs) / 1000;
            if (newOvl < 16)
                newOvl = 16;

            // must be divisible by 8
            newOvl -= newOvl % 8;

            AcceptNewOverlapLength(newOvl);
        }

        private double CalcCrossCorr(in ReadOnlySpan<float> mixingPos, in ReadOnlySpan<float> compare, ref double anorm)
        {
            double corr;
            double norm;
            int i;

            corr = norm = 0;

            // hint compiler autovectorization that loop length is divisible by 8
            int ilength = (_channels * _overlapLength) & -8;

            // Same routine for stereo and mono.
            for (i = 0; i < ilength; i++)
            {
                corr += mixingPos[i] * compare[i];
                norm += mixingPos[i] * mixingPos[i];
            }

            anorm = norm;
            return corr / Math.Sqrt(norm < 1e-9 ? 1.0 : norm);
        }

        private unsafe double CalcCrossCorrAccumulate(in ReadOnlySpan<float> mixingPos, in ReadOnlySpan<float> compare, ref double norm)
        {
            double corr;
            int i;

            corr = 0;

            // cancel first normalizer tap from previous round
            fixed (float* historyPos = mixingPos)
            {
                for (i = 1; i <= _channels; i++)
                {
                    norm -= historyPos[-i] * historyPos[-i];
                }
            }

            // hint compiler autovectorization that loop length is divisible by 8
            int ilength = (_channels * _overlapLength) & -8;

            // Same routine for stereo and mono.
            for (i = 0; i < ilength; i++)
            {
                corr += mixingPos[i] * compare[i];
            }

            // update normalizer with last samples of this round
            for (int j = 0; j < _channels; j++)
            {
                i--;
                norm += mixingPos[i] * mixingPos[i];
            }

            return corr / Math.Sqrt(norm < 1e-9 ? 1.0 : norm);
        }

        private int SeekBestOverlapPositionFull(in ReadOnlySpan<float> refPos)
        {
            int bestOffs;
            double bestCorr;
            int i;
            double norm = 0;

            bestOffs = 0;

            // Scans for the best correlation value by testing each possible position
            // over the permitted range.
            bestCorr = CalcCrossCorr(refPos, _pMidBuffer, ref norm);
            bestCorr = (bestCorr + 0.1) * 0.75;

            object criticalSection = new object();

            for (i = 1; i < _seekLength; i++)
            {
                double corr;

                // Calculates correlation value for the mixing position corresponding to 'i'
#if Parallel
                // in parallel OpenMP mode, can't use norm accumulator version as parallel executor won't
                // iterate the loop in sequential order
                corr = CalcCrossCorr(refPos.Slice(channels * i), pMidBuffer, ref norm);
#else
                // In non-parallel version call "calcCrossCorrAccumulate" that is otherwise same
                // as "calcCrossCorr", but saves time by reusing & updating previously stored
                // "norm" value
                corr = CalcCrossCorrAccumulate(refPos.Slice(_channels * i), _pMidBuffer, ref norm);
#endif

                // heuristic rule to slightly favour values close to mid of the range
                double tmp = ((2 * i) - _seekLength) / (double)_seekLength;
                corr = (corr + 0.1) * (1.0 - (0.25 * tmp * tmp));

                // Checks for the highest correlation value
                if (corr > bestCorr)
                {
                    // For optimal performance, enter critical section only in case that best value found.
                    // in such case repeat 'if' condition as it's possible that parallel execution may have
                    // updated the bestCorr value in the mean time
                    lock (criticalSection)
                    {
#pragma warning disable S2589 // Boolean expressions should not be gratuitous
                        if (corr > bestCorr)
#pragma warning restore S2589 // Boolean expressions should not be gratuitous
                        {
                            bestCorr = corr;
                            bestOffs = i;
                        }
                    }
                }
            }

            // clear cross correlation routine state if necessary (is so e.g. in MMX routines).
            ClearCrossCorrState();

            return bestOffs;
        }

        private int SeekBestOverlapPositionQuick(in ReadOnlySpan<float> refPos)
        {
            const int SCANSTEP = 16;
            const int SCANWIND = 8;

            int bestOffs;
            int i;
            int bestOffs2;
            float bestCorr, corr;
            float bestCorr2;
            double norm = 0;

            // note: 'float' types used in this function in case that the platform would need to use software-fp
            bestCorr =
            bestCorr2 = float.MinValue;
            bestOffs =
            bestOffs2 = SCANWIND;

            // Scans for the best correlation value by testing each possible position
            // over the permitted range. Look for two best matches on the first pass to
            // increase possibility of ideal match.
            //
            // Begin from "SCANSTEP" instead of SCANWIND to make the calculation
            // catch the 'middlepoint' of seekLength vector as that's the a-priori
            // expected best match position
            //
            // Roughly:
            // - 15% of cases find best result directly on the first round,
            // - 75% cases find better match on 2nd round around the best match from 1st round
            // - 10% cases find better match on 2nd round around the 2nd-best-match from 1st round
            for (i = SCANSTEP; i < _seekLength - SCANWIND - 1; i += SCANSTEP)
            {
                // Calculates correlation value for the mixing position corresponding
                // to 'i'
                corr = (float)CalcCrossCorr(refPos.Slice(_channels * i), _pMidBuffer, ref norm);

                // heuristic rule to slightly favour values close to mid of the seek range
                float tmp = ((2 * i) - _seekLength - 1) / (float)_seekLength;
                corr = (corr + 0.1f) * (1.0f - (0.25f * tmp * tmp));

                // Checks for the highest correlation value
                if (corr > bestCorr)
                {
                    // found new best match. keep the previous best as 2nd best match
                    bestCorr2 = bestCorr;
                    bestOffs2 = bestOffs;
                    bestCorr = corr;
                    bestOffs = i;
                }
                else if (corr > bestCorr2)
                {
                    // not new best, but still new 2nd best match
                    bestCorr2 = corr;
                    bestOffs2 = i;
                }
            }

            // Scans surroundings of the found best match with small stepping
            int end = Math.Min(bestOffs + SCANWIND + 1, _seekLength);
            for (i = bestOffs - SCANWIND; i < end; i++)
            {
                if (i == bestOffs)
                    continue;    // this offset already calculated, thus skip

                // Calculates correlation value for the mixing position corresponding
                // to 'i'
                corr = (float)CalcCrossCorr(refPos.Slice(_channels * i), _pMidBuffer, ref norm);

                // heuristic rule to slightly favour values close to mid of the range
                float tmp = ((2 * i) - _seekLength - 1) / (float)_seekLength;
                corr = (corr + 0.1f) * (1.0f - (0.25f * tmp * tmp));

                // Checks for the highest correlation value
                if (corr > bestCorr)
                {
                    bestCorr = corr;
                    bestOffs = i;
                }
            }

            // Scans surroundings of the 2nd best match with small stepping
            end = Math.Min(bestOffs2 + SCANWIND + 1, _seekLength);
            for (i = bestOffs2 - SCANWIND; i < end; i++)
            {
                if (i == bestOffs2)
                    continue;    // this offset already calculated, thus skip

                // Calculates correlation value for the mixing position corresponding
                // to 'i'
                corr = (float)CalcCrossCorr(refPos.Slice(_channels * i), _pMidBuffer, ref norm);

                // heuristic rule to slightly favour values close to mid of the range
                float tmp = ((2 * i) - _seekLength - 1) / (float)_seekLength;
                corr = (corr + 0.1f) * (1.0f - (0.25f * tmp * tmp));

                // Checks for the highest correlation value
                if (corr > bestCorr)
                {
                    bestCorr = corr;
                    bestOffs = i;
                }
            }

            // clear cross correlation routine state if necessary (is so e.g. in MMX routines).
            ClearCrossCorrState();

            return bestOffs;
        }

        private int SeekBestOverlapPosition(in ReadOnlySpan<float> refPos)
        {
            if (_bQuickSeek)
            {
                return SeekBestOverlapPositionQuick(refPos);
            }
            else
            {
                return SeekBestOverlapPositionFull(refPos);
            }
        }

        [Pure]
        private void OverlapStereo(in Span<float> output, in ReadOnlySpan<float> input)
        {
            int i;
            float fScale;
            float f1;
            float f2;

            fScale = 1.0f / _overlapLength;

            f1 = 0;
            f2 = 1.0f;

            for (i = 0; i < 2 * _overlapLength; i += 2)
            {
                output[i + 0] = (input[i + 0] * f1) + (_pMidBuffer[i + 0] * f2);
                output[i + 1] = (input[i + 1] * f1) + (_pMidBuffer[i + 1] * f2);

                f1 += fScale;
                f2 -= fScale;
            }
        }

        [Pure]
        private void OverlapMono(in Span<float> output, in ReadOnlySpan<float> input)
        {
            int i;
            float m1, m2;

            m1 = 0;
            m2 = _overlapLength;

            for (i = 0; i < _overlapLength; i++)
            {
                output[i] = ((input[i] * m1) + (_pMidBuffer[i] * m2)) / _overlapLength;
                m1 += 1;
                m2 -= 1;
            }
        }

        [Pure]
        private void OverlapMulti(in Span<float> output, in ReadOnlySpan<float> input)
        {
            int i;
            float fScale;
            float f1;
            float f2;

            fScale = 1.0f / _overlapLength;

            f1 = 0;
            f2 = 1.0f;

            i = 0;
            for (int i2 = 0; i2 < _overlapLength; i2++)
            {
                // note: Could optimize this slightly by taking into account that always channels > 2
                for (int c = 0; c < _channels; c++)
                {
                    output[i] = (input[i] * f1) + (_pMidBuffer[i] * f2);
                    i++;
                }

                f1 += fScale;
                f2 -= fScale;
            }
        }

        private void ClearMidBuffer() => _pMidBuffer.AsSpan().Slice(0, _channels * _overlapLength).Clear();

        [Pure]
        private void Overlap(in Span<float> output, in ReadOnlySpan<float> input, int ovlPos)
        {
#if !USE_MULTICH_ALWAYS
            if (_channels == 1)
            {
                // mono sound.
                OverlapMono(output, input.Slice(ovlPos));
            }
            else if (_channels == 2)
            {
                // stereo sound
                OverlapStereo(output, input.Slice(2 * ovlPos));
            }
            else
#endif // USE_MULTICH_ALWAYS
            {
                Debug.Assert(_channels > 0, "Multiple channels");
                OverlapMulti(output, input.Slice(_channels * ovlPos));
            }
        }

        private void CalcSeqParameters()
        {
            // Adjust tempo param according to tempo, so that variating processing sequence length is used
            // at various tempo settings, between the given low...top limits
            const double AUTOSEQ_TEMPO_LOW = 0.5;     // auto setting low tempo range (-50%)
            const double AUTOSEQ_TEMPO_TOP = 2.0;     // auto setting top tempo range (+100%)

            // sequence-ms setting values at above low & top tempo
            const double AUTOSEQ_AT_MIN = 90.0;
            const double AUTOSEQ_AT_MAX = 40.0;
            const double AUTOSEQ_K = (AUTOSEQ_AT_MAX - AUTOSEQ_AT_MIN) / (AUTOSEQ_TEMPO_TOP - AUTOSEQ_TEMPO_LOW);
            const double AUTOSEQ_C = AUTOSEQ_AT_MIN - (AUTOSEQ_K * AUTOSEQ_TEMPO_LOW);

            // seek-window-ms setting values at above low & top tempoq
            const double AUTOSEEK_AT_MIN = 20.0;
            const double AUTOSEEK_AT_MAX = 15.0;
            const double AUTOSEEK_K = (AUTOSEEK_AT_MAX - AUTOSEEK_AT_MIN) / (AUTOSEQ_TEMPO_TOP - AUTOSEQ_TEMPO_LOW);
            const double AUTOSEEK_C = AUTOSEEK_AT_MIN - (AUTOSEEK_K * AUTOSEQ_TEMPO_LOW);

            static double CHECK_LIMITS(double x, double mi, double ma)
                => x switch
                {
                    _ when x < mi => mi,
                    _ when x > ma => ma,
                    _ => x
                };

            double seq, seek;

            if (_bAutoSeqSetting)
            {
                seq = AUTOSEQ_C + (AUTOSEQ_K * _tempo);
                seq = CHECK_LIMITS(seq, AUTOSEQ_AT_MAX, AUTOSEQ_AT_MIN);
                _sequenceMs = (int)(seq + 0.5);
            }

            if (_bAutoSeekSetting)
            {
                seek = AUTOSEEK_C + (AUTOSEEK_K * _tempo);
                seek = CHECK_LIMITS(seek, AUTOSEEK_AT_MAX, AUTOSEEK_AT_MIN);
                _seekWindowMs = (int)(seek + 0.5);
            }

            // Update seek window lengths
            _seekWindowLength = (_sampleRate * _sequenceMs) / 1000;
            if (_seekWindowLength < 2 * _overlapLength)
            {
                _seekWindowLength = 2 * _overlapLength;
            }

            _seekLength = (_sampleRate * _seekWindowMs) / 1000;
        }

        private void AdaptNormalizer()
        {
            // Do not adapt normalizer over too silent sequences to avoid averaging filter depleting to
            // too low values during pauses in music
            if ((_maxnorm > 1000) || (_maxnormf > 40000000))
            {
                // norm averaging filter
                _maxnormf = (0.9f * _maxnormf) + (0.1f * _maxnorm);

                if ((_maxnorm > 800000000) && (_overlapDividerBitsNorm < 16))
                {
                    // large values, so increase divider
                    _overlapDividerBitsNorm++;
                    if (_maxnorm > 1600000000)
                        _overlapDividerBitsNorm++; // extra large value => extra increase
                }
                else if ((_maxnormf < 1000000) && (_overlapDividerBitsNorm > 0))
                {
                    // extra small values, decrease divider
                    _overlapDividerBitsNorm--;
                }
            }

            _maxnorm = 0;
        }

        /// <summary>
        /// Changes the tempo of the given sound samples.
        /// Returns amount of samples returned in the "output" buffer.
        /// The maximum amount of samples that can be returned at a time is set by
        /// the 'set_returnBuffer_size' function.
        /// </summary>
        private void ProcessSamples()
        {
            int ovlSkip;
            int offset = 0;
            int temp;

            // Process samples as long as there are enough samples in 'inputBuffer'
            // to form a processing frame.
            while (_inputBuffer.AvailableSamples >= _sampleReq)
            {
                if (!_isBeginning)
                {
                    // apart from the very beginning of the track,
                    // scan for the best overlapping position & do overlap-add
                    offset = SeekBestOverlapPosition(_inputBuffer.PtrBegin());

                    // Mix the samples in the 'inputBuffer' at position of 'offset' with the
                    // samples in 'midBuffer' using sliding overlapping
                    // ... first partially overlap with the end of the previous sequence
                    // (that's in 'midBuffer')
                    Overlap(_outputBuffer.PtrEnd(_overlapLength), _inputBuffer.PtrBegin(), offset);
                    _outputBuffer.PutSamples(_overlapLength);
                    offset += _overlapLength;
                }
                else
                {
                    // Adjust processing offset at beginning of track by not perform initial overlapping
                    // and compensating that in the 'input buffer skip' calculation
                    _isBeginning = false;
                    int skip = (int)((_tempo * _overlapLength) + (0.5 * _seekLength) + 0.5);

#if SOUNDTOUCH_ALLOW_NONEXACT_SIMD_OPTIMIZATION
#if SOUNDTOUCH_ALLOW_SSE
                    // if SSE mode, round the skip amount to value corresponding to aligned memory address
                    if (channels == 1)
                    {
                        skip &= -4;
                    }
                    else if (channels == 2)
                    {
                        skip &= -2;
                    }
#endif
#endif
                    _skipFract -= skip;
                    Debug.Assert(_nominalSkip >= -_skipFract, "_nominalSkip >= -_skipFract");
                }

                // ... then copy sequence samples from 'inputBuffer' to output:

                // crosscheck that we don't have buffer overflow...
                if (_inputBuffer.AvailableSamples < (offset + _seekWindowLength - _overlapLength))
                {
                    continue;    // just in case, shouldn't really happen
                }

                // length of sequence
                temp = _seekWindowLength - (2 * _overlapLength);
                _outputBuffer.PutSamples(_inputBuffer.PtrBegin().Slice(_channels * offset), temp);

                // Copies the end of the current sequence from 'inputBuffer' to
                // 'midBuffer' for being mixed with the beginning of the next
                // processing sequence and so on.
                Debug.Assert((offset + temp + _overlapLength) <= _inputBuffer.AvailableSamples, "Current position does not exceed available samples.");

                _inputBuffer.PtrBegin().Slice(_channels * (offset + temp), _channels * _overlapLength).CopyTo(_pMidBuffer);

                // Remove the processed samples from the input buffer. Update
                // the difference between integer & nominal skip step to 'skipFract'
                // in order to prevent the error from accumulating over time.
                _skipFract += _nominalSkip;   // real skip size
                ovlSkip = (int)_skipFract;   // rounded to integer skip
                _skipFract -= ovlSkip;       // maintain the fraction part, i.e. real vs. integer skip
                _inputBuffer.ReceiveSamples(ovlSkip);
            }
        }
    }
}
