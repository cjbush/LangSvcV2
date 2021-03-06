﻿namespace Tvl.VisualStudio.Language.Antlr3
{
    using System;
    using Antlr.Runtime;
    using global::Antlr3.Grammars;
    using global::Antlr3.Tool;
    using Microsoft.VisualStudio.Text;
    using Tvl.VisualStudio.Language.Parsing;

    internal class AntlrErrorProvidingParser : ANTLRParser
    {
        public event EventHandler<ParseErrorEventArgs> ParseError;

        public AntlrErrorProvidingParser(ITokenStream input)
            : base(input)
        {
        }

        public override void DisplayRecognitionError(string[] tokenNames, RecognitionException e)
        {
            string header = GetErrorHeader(e);
            string message = GetErrorMessage(e, tokenNames);
            Span span = new Span();
            if (e.Token != null)
                span = Span.FromBounds(e.Token.StartIndex, e.Token.StopIndex + 1);

            ParseErrorEventArgs args = new ParseErrorEventArgs(message, span);
            OnParseError(args);

            base.DisplayRecognitionError(tokenNames, e);
        }

        protected virtual void OnParseError(ParseErrorEventArgs e)
        {
            var t = ParseError;
            if (t != null)
                t(this, e);
        }

        public sealed class ErrorListener : IANTLRErrorListener
        {
            public void Error(ToolMessage msg)
            {
            }

            public void Error(Message msg)
            {
                GrammarSyntaxMessage syntaxMessage = msg as GrammarSyntaxMessage;
                if (syntaxMessage != null)
                {
                    IToken token = syntaxMessage.offendingToken;
                    if (token == null)
                        return;

                    AntlrParserTokenStream stream = syntaxMessage.exception.Input as AntlrParserTokenStream;
                    if (stream == null)
                        return;

                    var parser = stream.Parser;
                    if (parser == null)
                        return;

                    Span span = Span.FromBounds(token.StartIndex, token.StopIndex + 1);

                    ParseErrorEventArgs e = new ParseErrorEventArgs(syntaxMessage.ToString(), span);
                    parser.OnParseError(e);
                    return;
                }
            }

            public void Info(string msg)
            {
            }

            public void Warning(Message msg)
            {
            }
        }
    }
}
