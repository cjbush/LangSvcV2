﻿namespace Tvl.VisualStudio.Text.Tagging
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Diagnostics.Contracts;
    using System.Linq;
    using Microsoft.VisualStudio.Language.StandardClassification;
    using Microsoft.VisualStudio.Text;
    using Microsoft.VisualStudio.Text.Classification;
    using Microsoft.VisualStudio.Text.Editor;
    using Microsoft.VisualStudio.Text.Tagging;
    using Tvl.Threading;
    using System.Threading;

    public sealed class BraceMatchingTagger : ITagger<TextMarkerTag>
    {
        private volatile int _requestNumber;
        private object _updateLock = new object();

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public BraceMatchingTagger(ITextView textView, ITextBuffer sourceBuffer, IClassifier aggregator, IEnumerable<KeyValuePair<char, char>> matchingCharacters)
        {
            if (textView == null)
                throw new ArgumentNullException("text");
            if (sourceBuffer == null)
                throw new ArgumentNullException("sourceBuffer");
            if (aggregator == null)
                throw new ArgumentNullException("aggregator");
            if (matchingCharacters == null)
                throw new ArgumentNullException("matchingCharacters");
            Contract.EndContractBlock();

            this.TextView = textView;
            this.SourceBuffer = sourceBuffer;
            this.Aggregator = aggregator;
            this.MatchingCharacters = matchingCharacters.ToList().AsReadOnly();

            this.TextView.Caret.PositionChanged += OnCaretPositionChanged;
            this.TextView.LayoutChanged += OnTextViewLayoutChanged;
        }

        public ITextView TextView
        {
            get;
            private set;
        }

        public ITextBuffer SourceBuffer
        {
            get;
            private set;
        }

        public IClassifier Aggregator
        {
            get;
            private set;
        }

        public ReadOnlyCollection<KeyValuePair<char, char>> MatchingCharacters
        {
            get;
            private set;
        }

        private SnapshotPoint? CurrentChar
        {
            get;
            set;
        }

        private IEnumerable<ITagSpan<TextMarkerTag>> Tags
        {
            get;
            set;
        }

        private static bool IsInCommentOrLiteral(IClassifier aggregator, SnapshotPoint point, PositionAffinity affinity)
        {
            Contract.Requires(aggregator != null);

            // TODO: handle affinity
            SnapshotSpan span = new SnapshotSpan(point, 1);

            var classifications = aggregator.GetClassificationSpans(span);
            var relevant = classifications.FirstOrDefault(classificationSpan => classificationSpan.Span.Contains(point));
            if (relevant == null || relevant.ClassificationType == null)
                return false;

            return relevant.ClassificationType.IsOfType(PredefinedClassificationTypeNames.Comment)
                || relevant.ClassificationType.IsOfType(PredefinedClassificationTypeNames.Literal);
        }

        private bool FindMatchingCloseChar(int revision, SnapshotPoint start, IClassifier aggregator, char open, char close, int maxLines, out SnapshotSpan pairSpan)
        {
            pairSpan = new SnapshotSpan(start.Snapshot, 1, 1);
            ITextSnapshotLine line = start.GetContainingLine();
            string lineText = line.GetText();
            int lineNumber = line.LineNumber;
            int offset = start.Position - line.Start.Position + 1;

            int stopLineNumber = start.Snapshot.LineCount - 1;
            if (maxLines > 0)
                stopLineNumber = Math.Min(stopLineNumber, lineNumber + maxLines);

            int openCount = 0;
            while (true)
            {
                while (offset < line.Length)
                {
                    if (_requestNumber != revision)
                    {
                        return false;
                    }

                    char currentChar = lineText[offset];
                    // TODO: is this the correct affinity
                    if (currentChar == close && !IsInCommentOrLiteral(aggregator, new SnapshotPoint(start.Snapshot, offset + line.Start.Position), PositionAffinity.Successor))
                    {
                        if (openCount > 0)
                        {
                            openCount--;
                        }
                        else
                        {
                            pairSpan = new SnapshotSpan(start.Snapshot, line.Start + offset, 1);
                            return true;
                        }
                    }
                    // TODO: is this the correct affinity
                    else if (currentChar == open && !IsInCommentOrLiteral(aggregator, new SnapshotPoint(start.Snapshot, offset + line.Start.Position), PositionAffinity.Successor))
                    {
                        openCount++;
                    }

                    offset++;
                }

                // move on to the next line
                lineNumber++;
                if (lineNumber > stopLineNumber)
                    break;

                line = line.Snapshot.GetLineFromLineNumber(lineNumber);
                lineText = line.GetText();
                offset = 0;
            }

            return false;
        }

        private bool FindMatchingOpenChar(int revision, SnapshotPoint start, IClassifier aggregator, char open, char close, int maxLines, out SnapshotSpan pairSpan)
        {
            pairSpan = new SnapshotSpan(start, start);
            ITextSnapshotLine line = start.GetContainingLine();
            int lineNumber = line.LineNumber;
            int offset = start - line.Start - 1;

            // if the offset is negative, move to the previous line
            if (offset < 0)
            {
                lineNumber--;
                line = line.Snapshot.GetLineFromLineNumber(lineNumber);
                offset = line.Length - 1;
            }

            string lineText = line.GetText();

            int stopLineNumber = 0;
            if (maxLines > 0)
                stopLineNumber = Math.Max(stopLineNumber, lineNumber - maxLines);

            int closeCount = 0;
            while (true)
            {
                while (offset >= 0)
                {
                    if (_requestNumber != revision)
                    {
                        return false;
                    }

                    char currentChar = lineText[offset];
                    // TODO: is this the correct affinity
                    if (currentChar == open && !IsInCommentOrLiteral(aggregator, new SnapshotPoint(start.Snapshot, offset + line.Start.Position), PositionAffinity.Successor))
                    {
                        if (closeCount > 0)
                        {
                            closeCount--;
                        }
                        else
                        {
                            pairSpan = new SnapshotSpan(line.Start + offset, 1);
                            return true;
                        }
                    }
                    // TODO: is this the correct affinity
                    else if (currentChar == close && !IsInCommentOrLiteral(aggregator, new SnapshotPoint(start.Snapshot, offset + line.Start.Position), PositionAffinity.Successor))
                    {
                        closeCount++;
                    }

                    offset--;
                }

                // move to the previous line
                lineNumber--;
                if (lineNumber < stopLineNumber)
                    break;

                line = line.Snapshot.GetLineFromLineNumber(lineNumber);
                lineText = line.GetText();
                offset = line.Length - 1;
            }

            return false;
        }

        private void OnTagsChanged(SnapshotSpanEventArgs e)
        {
            var t = TagsChanged;
            if (t != null)
                t(this, e);
        }

        private bool IsMatchStartCharacter(char c)
        {
            return MatchingCharacters.Any(pair => pair.Key == c);
        }

        private bool IsMatchCloseCharacter(char c)
        {
            return MatchingCharacters.Any(pair => pair.Value == c);
        }

        private char GetMatchCloseCharacter(char c)
        {
            return MatchingCharacters.First(pair => pair.Key == c).Value;
        }

        private char GetMatchOpenCharacter(char c)
        {
            return MatchingCharacters.First(pair => pair.Value == c).Key;
        }

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            return Tags;
        }

        private void UpdateAtCaretPosition(CaretPosition caretPosition)
        {
            var revision = Interlocked.Increment(ref _requestNumber);

            CurrentChar = caretPosition.Point.GetPoint(SourceBuffer, caretPosition.Affinity);
            if (!CurrentChar.HasValue)
            {
                if (!TryClearResults(revision))
                    return;
            }
            else
            {
                if (!TrySynchronousUpdate(revision))
                {
                    if (!TryClearResults(revision))
                        return;

                    QueueAsynchronousUpdate(revision, CurrentChar.Value);
                }
            }

            var t = TagsChanged;
            if (t != null)
                t(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
        }

        private void QueueAsynchronousUpdate(int revision, SnapshotPoint point)
        {
            Action action =
                () =>
                {
                    try
                    {
                        IEnumerable<TagSpan<TextMarkerTag>> tags;
                        if (TryRecalculateTags(revision, point, 0, out tags))
                        {
                            if (TrySaveResults(revision, tags))
                            {
                                var t = TagsChanged;
                                if (t != null)
                                    t(this, new SnapshotSpanEventArgs(new SnapshotSpan(point.Snapshot, 0, point.Snapshot.Length)));
                            }
                        }
                    }
                    catch
                    {
                    }
                };

            action.BeginInvoke(null, null);
        }

        private bool TryClearResults(int revision)
        {
            lock (this._updateLock)
            {
                if (_requestNumber != revision)
                    return false;

                this.Tags = new ITagSpan<TextMarkerTag>[0];
                return true;
            }
        }

        private bool TrySaveResults(int revision, IEnumerable<ITagSpan<TextMarkerTag>> tags)
        {
            lock (this._updateLock)
            {
                if (_requestNumber != revision)
                    return false;

                this.Tags = tags;
                return true;
            }
        }

        private bool TrySynchronousUpdate(int revision)
        {
            // don't do anything if the current SnapshotPoint is not initialized or at the end of the buffer
            if (!CurrentChar.HasValue || CurrentChar.Value.Position >= CurrentChar.Value.Snapshot.Length)
            {
                return TryClearResults(revision);
            }

            // hold on to a snapshot of the current character
            var currentChar = CurrentChar.Value;

            IEnumerable<TagSpan<TextMarkerTag>> tags;
            if (TryRecalculateTags(revision, currentChar, TextView.TextViewLines.Count, out tags))
            {
                return TrySaveResults(revision, tags);
            }

            return false;
        }

        private bool TryRecalculateTags(int revision, SnapshotPoint point, int searchDistance, out IEnumerable<TagSpan<TextMarkerTag>> tags)
        {
            // get the current char and the previous char
            char currentText = point.GetChar();
            // if current char is 0 (beginning of buffer), don't move it back
            SnapshotPoint lastChar = point == 0 ? point : point - 1;
            char lastText = lastChar.GetChar();
            SnapshotSpan pairSpan = new SnapshotSpan();

            if (IsMatchStartCharacter(currentText) && !IsInCommentOrLiteral(Aggregator, point, TextView.Caret.Position.Affinity))
            {
                char closeChar = GetMatchCloseCharacter(currentText);
                /* TODO: Need to improve handling of larger blocks. this won't highlight if the matching brace is more
                 *       than 1 screen's worth of lines away. Changing this to 10 * TextView.TextViewLines.Count seemed
                 *       to improve the situation.
                 */
                if (FindMatchingCloseChar(revision, point, Aggregator, currentText, closeChar, searchDistance, out pairSpan))
                {
                    tags = new TagSpan<TextMarkerTag>[]
                        {
                            new TagSpan<TextMarkerTag>(new SnapshotSpan(point, 1), PredefinedTextMarkerTags.BraceHighlight),
                            new TagSpan<TextMarkerTag>(pairSpan, PredefinedTextMarkerTags.BraceHighlight)
                        };

                    return true;
                }
                else
                {
                    tags = new TagSpan<TextMarkerTag>[0];
                    return false;
                }
            }
            else if (IsMatchCloseCharacter(lastText) && !IsInCommentOrLiteral(Aggregator, point, TextView.Caret.Position.Affinity))
            {
                var open = GetMatchOpenCharacter(lastText);
                if (FindMatchingOpenChar(revision, lastChar, Aggregator, open, lastText, searchDistance, out pairSpan))
                {
                    tags = new TagSpan<TextMarkerTag>[]
                        {
                            new TagSpan<TextMarkerTag>(new SnapshotSpan(lastChar, 1), PredefinedTextMarkerTags.BraceHighlight),
                            new TagSpan<TextMarkerTag>(pairSpan, PredefinedTextMarkerTags.BraceHighlight)
                        };

                    return true;
                }
                else
                {
                    tags = new TagSpan<TextMarkerTag>[0];
                    return false;
                }
            }
            else
            {
                // successfully identified that there are no matching braces
                tags = new TagSpan<TextMarkerTag>[0];
                return true;
            }
        }

        private void OnTextViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (e.NewSnapshot != e.OldSnapshot)
                UpdateAtCaretPosition(TextView.Caret.Position);
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            UpdateAtCaretPosition(e.NewPosition);
        }
    }
}
