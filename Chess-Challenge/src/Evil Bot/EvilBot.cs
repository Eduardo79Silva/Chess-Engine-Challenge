using System;
using System.Linq;
using ChessChallenge.API;

public class EvilBot : IChessBot
{

    //private const byte INVALID = 0, EXACT = 1, LOWERBOUND = 2, UPPERBOUND = 3;

    //14 bytes per entry, likely will align to 16 bytes due to padding (if it aligns to 32, recalculate max TP table size)
    private readonly TTEntry[] tt = new TTEntry[0x400000];
    private record struct TTEntry(ulong _key, Move _move, int _depth, int _score, int _bound);


    private int timeLimit;
    Timer globalTimer;

    private int[,,] history;
    private Move[] killers = new Move[52];

    int[] moveScores = new int[218];

    private bool cancelSearch => globalTimer.MillisecondsElapsedThisTurn > timeLimit;


    Board globalBoard;
    Move bestMove;



    public Move Think(Board board, Timer timer)
    {
        globalBoard = board;
        globalTimer = timer;

        history = new int[2, 7, 64];


        timeLimit = timer.MillisecondsRemaining / 30;



        for (int depth = 2, alpha = -9999999, beta = 9999999, eval; ;)
        {
            eval = Negamax(depth, 0, alpha, beta, true);

            if (cancelSearch)
                return bestMove;

            if (eval <= alpha) alpha -= 62;
            else if (eval >= beta) beta += 62;
            else
            {

                alpha = eval - 17;
                beta = eval + 17;
                depth++;
            }


        }
    }

    #region Search

    private int Negamax(int depth, int plyFromRoot, int alpha, int beta, bool doNull)
    {

        bool inCheck = globalBoard.IsInCheck(),
            canPrune = false,
            isPV = beta - alpha > 1,
            isRoot = plyFromRoot++ == 0;


        if (!isRoot && globalBoard.IsRepeatedPosition() || plyFromRoot > 50) return 0;

        // TT cutoffs
        ulong zHash = globalBoard.ZobristKey;
        ref TTEntry entry = ref tt[zHash & 0x3FFFFF];

        int bestEval = -9999999,
            movesTried = 0,
            movesScoreGuesses = 0,
            entryScore = entry._score,
            entryFlag = entry._bound,
            originalAlpha = alpha,
            eval;

        int Search(int newAlpha, int R = 1, bool doNull = true) => eval = -Negamax(depth - R, plyFromRoot, -newAlpha, -alpha, doNull);

        if (entry._key == zHash && !isRoot && entry._depth >= depth && (
                // Exact
                entryFlag == 1 ||
                // Upperbound
                entryFlag == 2 && entryScore <= alpha ||
                // Lowerbound
                entryFlag == 3 && entryScore >= beta))
            return entryScore;

        if (inCheck) depth++;


        bool qsearch = depth <= 0;
        if (qsearch)
        {
            bestEval = Evaluate();

            alpha = Math.Max(alpha, bestEval);
            if (alpha >= beta)
                return bestEval;
        }
        else if (!isPV && !inCheck)
        {
            // Static eval calculation for pruning
            // Static Move Pruning
            int staticEval = Evaluate();
            if (depth <= 10 && staticEval - 96 * depth >= beta) return staticEval;

            // Null Move Pruning
            if (depth >= 2 && doNull)
            {
                globalBoard.TrySkipTurn();
                Search(beta, 3 + depth / 5, false);
                globalBoard.UndoSkipTurn();
                // Failed high on the null move
                if (eval >= beta)
                    return eval;
            }

            canPrune = depth <= 8 && staticEval + depth * 141 <= alpha;
        }

        Span<Move> moveSpan = stackalloc Move[218];
        globalBoard.GetLegalMovesNonAlloc(ref moveSpan, !inCheck && qsearch);

        // Order moves in reverse order -> negative values are ordered higher hence the strange equations

        foreach (Move move in moveSpan)
            moveScores[movesScoreGuesses++] = -(
            // Hash move
            move == entry._move ? 9_000_000 :
            // Promotions
            //move.IsPromotion ? 10000 :
            // MVVLVA
            move.IsCapture ? 1_000_000 * (int)move.CapturePieceType - (int)move.MovePieceType :
            // Killers
            killers[plyFromRoot] == move ? 900_000 :
            // History
            history[plyFromRoot & 1, (int)move.MovePieceType, move.TargetSquare.Index]);

        moveScores.AsSpan(0, moveSpan.Length).Sort(moveSpan);

        if (!qsearch && moveSpan.IsEmpty)
            return inCheck ? plyFromRoot - 99999 : 0;



        Move bestMoveInThisPosition = default;
        bestMove = isRoot ? moveSpan[0] : bestMove;
        foreach (Move move in moveSpan)
        {
            bool tactical = movesTried == 0 || move.IsCapture || move.IsPromotion;
            if (canPrune && !tactical)
                continue;

            globalBoard.MakeMove(move);


            if (movesTried++ == 0 || qsearch)
                // Always search first node with full depth
                Search(beta);
            else if ((isPV || tactical || movesTried < 6 || depth < 3
                    ? eval = alpha + 1
                    : Search(alpha + 1, 3)) > alpha &&

                    // If alpha was above threshold, update eval with a search with a null window
                    alpha < Search(alpha + 1))
                // We raised alpha on the null window search, research with no null window
                Search(beta);
            globalBoard.UndoMove(move);

            if (eval > bestEval)
            {
                bestEval = eval;
                bestMoveInThisPosition = move;

                if (isRoot) bestMove = move;

                alpha = Math.Max(alpha, eval);

                if (alpha >= beta)
                {
                    if (!move.IsCapture)
                    {
                        history[plyFromRoot & 1, (int)move.MovePieceType, move.TargetSquare.Index] += depth * depth;
                        killers[plyFromRoot] = move;
                    }
                    break;
                }
            }
            if (cancelSearch) return 9999999;
        }

        entry = new(
            zHash,
            bestMoveInThisPosition,
            depth,
            bestEval,
            bestEval >= beta ? 3 : bestEval <= originalAlpha ? 2 : 1);

        return bestEval;
    }

    #endregion

    #region Evaluation
    private readonly int[] GamePhaseIncrement = { 0, 1, 1, 2, 4, 0 };

    

    private readonly int[][] UnpackedPestoTables;

    public EvilBot()
    {
        // Big table packed with data from premade piece square tables
        // Access using using PackedEvaluationTables[square][pieceType] = score
        UnpackedPestoTables = new[] {
            63746705523041458768562654720m, 71818693703096985528394040064m, 75532537544690978830456252672m, 75536154932036771593352371712m, 76774085526445040292133284352m, 3110608541636285947269332480m, 936945638387574698250991104m, 75531285965747665584902616832m,
            77047302762000299964198997571m, 3730792265775293618620982364m, 3121489077029470166123295018m, 3747712412930601838683035969m, 3763381335243474116535455791m, 8067176012614548496052660822m, 4977175895537975520060507415m, 2475894077091727551177487608m,
            2458978764687427073924784380m, 3718684080556872886692423941m, 4959037324412353051075877138m, 3135972447545098299460234261m, 4371494653131335197311645996m, 9624249097030609585804826662m, 9301461106541282841985626641m, 2793818196182115168911564530m,
            77683174186957799541255830262m, 4660418590176711545920359433m, 4971145620211324499469864196m, 5608211711321183125202150414m, 5617883191736004891949734160m, 7150801075091790966455611144m, 5619082524459738931006868492m, 649197923531967450704711664m,
            75809334407291469990832437230m, 78322691297526401047122740223m, 4348529951871323093202439165m, 4990460191572192980035045640m, 5597312470813537077508379404m, 4980755617409140165251173636m, 1890741055734852330174483975m, 76772801025035254361275759599m,
            75502243563200070682362835182m, 78896921543467230670583692029m, 2489164206166677455700101373m, 4338830174078735659125311481m, 4960199192571758553533648130m, 3420013420025511569771334658m, 1557077491473974933188251927m, 77376040767919248347203368440m,
            73949978050619586491881614568m, 77043619187199676893167803647m, 1212557245150259869494540530m, 3081561358716686153294085872m, 3392217589357453836837847030m, 1219782446916489227407330320m, 78580145051212187267589731866m, 75798434925965430405537592305m,
            68369566912511282590874449920m, 72396532057599326246617936384m, 75186737388538008131054524416m, 77027917484951889231108827392m, 73655004947793353634062267392m, 76417372019396591550492896512m, 74568981255592060493492515584m, 70529879645288096380279255040m,
        }.Select(packedTable =>
        new System.Numerics.BigInteger(packedTable).ToByteArray().Take(12)
                    // Using search max time since it's an integer than initializes to zero and is assgined before being used again 
                    .Select(square => (int)((sbyte)square * 1.461) + PieceValues[timeLimit++ % 12])
                .ToArray()
        ).ToArray();
    }

    private int Evaluate()
    {
        int middlegame = 0, endgame = 0, gamephase = 0, sideToMove = 2;
        for (; --sideToMove >= 0; middlegame = -middlegame, endgame = -endgame)
        {
            for (int piece = -1, square; ++piece < 6;)
                for (ulong mask = globalBoard.GetPieceBitboard((PieceType)piece + 1, sideToMove > 0); mask != 0;)
                {
                    // Gamephase, middlegame -> endgame
                    gamephase += GamePhaseIncrement[piece];

                    // Material and square evaluation
                    square = BitboardHelper.ClearAndGetIndexOfLSB(ref mask) ^ 56 * sideToMove;
                    middlegame += UnpackedPestoTables[square][piece];
                    endgame += UnpackedPestoTables[square][piece + 6];

                    // Bishop pair bonus
                    if (piece == 2 && mask != 0)
                    {
                        middlegame += 22;
                        endgame += 30;
                    }
                }

        }


        // Apply the contempt factor adjustment to the evaluation
        return ((middlegame * gamephase + endgame * (24 - gamephase)) / 24 * (globalBoard.IsWhiteToMove ? 1 : -1) + gamephase / 2);
    }

    #endregion


}