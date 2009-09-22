﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using IndexConverter = System.Func<BLToolkit.Data.Linq.FieldIndex,BLToolkit.Data.Linq.FieldIndex>;

namespace BLToolkit.Data.Linq
{
	using DataProvider;
	using Mapping;
	using Reflection;
	using Data.Sql;

	class ExpressionParser : ReflectionHelper
	{
		// Should be single instance.
		//
		static protected readonly ParameterExpression ParametersParam = Expression.Parameter(typeof(object[]), "ps");
	}

	class ExpressionParser<T> : ExpressionParser
	{
		#region Init

		public ExpressionParser()
		{
			_info.Queries.Add(new ExpressionInfo<T>.QueryInfo());
		}

		readonly ExpressionInfo<T>   _info            = new ExpressionInfo<T>();
		readonly ParameterExpression _expressionParam = Expression.Parameter(typeof(Expression),        "expr");
		readonly ParameterExpression _contextParam    = Expression.Parameter(typeof(QueryContext),      "context");
		readonly ParameterExpression _dataReaderParam = Expression.Parameter(typeof(IDataReader),       "rd");
		readonly ParameterExpression _mapSchemaParam  = Expression.Parameter(typeof(MappingSchema),     "ms");
		readonly ParameterExpression _infoParam       = Expression.Parameter(typeof(ExpressionInfo<T>), "info");

		int    _currentSql = 0;
		Action _buildSelect;

		SqlBuilder CurrentSql
		{
			get { return _info.Queries[_currentSql].SqlBuilder;  }
			set { _info.Queries[_currentSql].SqlBuilder = value; }
		}

		List<ExpressionInfo<T>.Parameter> CurrentSqlParameters
		{
			get { return _info.Queries[_currentSql].Parameters; }
		}

		#endregion

		#region Parse

		public ExpressionInfo<T> Parse(
			DataProviderBase      dataProvider,
			MappingSchema         mappingSchema,
			Expression            expression,
			ParameterExpression[] parameters)
		{
			ParsingTracer.WriteLine(expression);
			ParsingTracer.IncIndentLevel();

			if (parameters != null)
				expression = ConvertParameters(expression, parameters);

			_info.DataProvider  = dataProvider;
			_info.MappingSchema = mappingSchema;
			_info.Expression    = expression;
			_info.Parameters    = parameters;

			ParseInfo.CreateRoot(expression, _expressionParam).Match(
				//
				// db.Select(() => ...)
				//
				pi => pi.IsLambda(0, body => { BuildScalarSelect(body); return true; }),
				//
				// db.Table.ToList()
				//
				pi => pi.IsConstant<IQueryable>((value,_) => SimpleQuery(value.ElementType, null)),
				//
				// from p in db.Table select p
				// db.Table.Select(p => p)
				//
				pi => pi.IsMethod(typeof(Queryable), "Select",
					obj => obj.IsConstant<IQueryable>(),
					arg => arg.IsLambda<T>(
						body => body.NodeType == ExpressionType.Parameter,
						l    => SimpleQuery(typeof(T), l.Expr.Parameters[0].Name))),
				//
				// everything else
				//
				pi =>
				{
					var query = ParseSequence(pi, null);

					if (_buildSelect != null)
						_buildSelect();
					else
						BuildSelect(query, SetQuery, SetQuery, i => i);

					return true;
				}
			);

			ParsingTracer.DecIndentLevel();
			return _info;
		}

		private Expression ConvertParameters(Expression expression, ParameterExpression[] parameters)
		{
			return ParseInfo.CreateRoot(expression, _expressionParam).Walk(pi =>
			{
				if (pi.NodeType == ExpressionType.Parameter)
				{
					var idx = Array.IndexOf(parameters, (ParameterExpression)pi.Expr);

					if (idx > 0)
						return pi.Parent.Replace(
							Expression.Convert(
								Expression.ArrayIndex(
									ParametersParam,
									Expression.Constant(Array.IndexOf(parameters, (ParameterExpression)pi.Expr))),
								pi.Expr.Type),
							pi.ParamAccessor);
				}

				return pi;
			});
		}

		QuerySource ParseSequence(ParseInfo info, QuerySource parent)
		{
			ParsingTracer.WriteLine(info);
			ParsingTracer.IncIndentLevel();

			var select = ParseTable(info);

			if (select != null)
			{
				ParsingTracer.DecIndentLevel();
				ParsingTracer.WriteLine(select);
				return select;
			}

			if (info.NodeType == ExpressionType.MemberAccess && TypeHelper.IsSameOrParent(typeof(IQueryable), info.Expr.Type))
			{
				info   = GetIQueriable(info);
				select = ParseTable(info);

				if (select != null)
				{
					ParsingTracer.DecIndentLevel();
					ParsingTracer.WriteLine(select);
					return select;
				}
			}

			if (info.NodeType != ExpressionType.Call)
				throw new ArgumentException(string.Format("Queryable method call expected. Got '{0}'.", info.Expr), "info");

			info.ConvertTo<MethodCallExpression>().Match
			(
				//
				// db.Table.Method()
				//
				pi => pi.IsQueryableMethod(seq =>
				{
					ParsingTracer.WriteLine(pi);
					ParsingTracer.IncIndentLevel();

					switch (pi.Expr.Method.Name)
					{
						case "Distinct"        : select = ParseSequence(seq, null); CurrentSql.Select.IsDistinct = true; break;
						case "First"           : select = ParseSequence(seq, null); CurrentSql.Select.Take(1); _info.MakeElementOperator(ElementMethod.First);           break;
						case "FirstOrDefault"  : select = ParseSequence(seq, null); CurrentSql.Select.Take(1); _info.MakeElementOperator(ElementMethod.FirstOrDefault);  break;
						case "Single"          : select = ParseSequence(seq, null); CurrentSql.Select.Take(2); _info.MakeElementOperator(ElementMethod.Single);          break;
						case "SingleOrDefault" : select = ParseSequence(seq, null); CurrentSql.Select.Take(2); _info.MakeElementOperator(ElementMethod.SingleOrDefault); break;
						case "Count"           : select = ParseSequence(seq, null); ParseAggregate(pi, null, select); break;
						default                :
							ParsingTracer.DecIndentLevel();
							return false;
					}

					ParsingTracer.DecIndentLevel();

					return true;
				}),
				//
				// db.Table.Method(l => ...)
				//
				pi => pi.IsQueryableMethod((seq,l) =>
				{
					switch (pi.Expr.Method.Name)
					{
						case "Select"            : select = ParseSequence(seq, null); select = ParseSelect    (l, select);               break;
						case "Where"             : select = ParseSequence(seq, null); select = ParseWhere     (l, select);               break;
						case "SelectMany"        : select = ParseSequence(seq, null); select = ParseSelectMany(l, null, select);         break;
						case "OrderBy"           : select = ParseSequence(seq, null); select = ParseOrderBy   (l, select, false, true);  break;
						case "OrderByDescending" : select = ParseSequence(seq, null); select = ParseOrderBy   (l, select, false, false); break;
						case "ThenBy"            : select = ParseSequence(seq, null); select = ParseOrderBy   (l, select, true,  true);  break;
						case "ThenByDescending"  : select = ParseSequence(seq, null); select = ParseOrderBy   (l, select, true,  false); break;
						case "GroupBy"           : select = ParseSequence(seq, null); select = ParseGroupBy   (l, null, null, select, pi.Expr.Type.GetGenericArguments()[0]); break;
						case "First"           : { select = ParseSequence(seq, null); select = ParseWhere     (l, select); CurrentSql.Select.Take(1); _info.MakeElementOperator(ElementMethod.First);           break; }
						case "FirstOrDefault"  : { select = ParseSequence(seq, null); select = ParseWhere     (l, select); CurrentSql.Select.Take(1); _info.MakeElementOperator(ElementMethod.FirstOrDefault);  break; }
						case "Single"          : { select = ParseSequence(seq, null); select = ParseWhere     (l, select); CurrentSql.Select.Take(2); _info.MakeElementOperator(ElementMethod.Single);          break; }
						case "SingleOrDefault" : { select = ParseSequence(seq, null); select = ParseWhere     (l, select); CurrentSql.Select.Take(2); _info.MakeElementOperator(ElementMethod.SingleOrDefault); break; }
						case "Count"           : { select = ParseSequence(seq, null); select = ParseWhere     (l, select); ParseAggregate(pi, null, select); break; }
						case "Min"             :
						case "Max"             :
						case "Average"         : { select = ParseSequence(seq, null); ParseAggregate(pi, l, select); break; }
						default                : return false;
					}
					return true;
				}),
				//
				// everything else
				//
				pi => pi.IsQueryableMethod ("SelectMany", 1, 2, seq => select = ParseSequence(seq, null), (l1, l2)        => select = ParseSelectMany(l1, l2, select)),
				pi => pi.IsQueryableMethod ("Join",             seq => select = ParseSequence(seq, null), (i, l2, l3, l4) => select = ParseJoin      (i,  l2, l3, l4, select)),
				pi => pi.IsQueryableMethod ("GroupJoin",        seq => select = ParseSequence(seq, null), (i, l2, l3, l4) => select = ParseGroupJoin (i,  l2, l3, l4, select)),
				pi => pi.IsQueryableMethod ("GroupBy",    1, 1, seq => select = ParseSequence(seq, null), (l1, l2)        => select = ParseGroupBy   (l1, l2,   null, select, pi.Expr.Type.GetGenericArguments()[0])),
				pi => pi.IsQueryableMethod ("GroupBy",    1, 2, seq => select = ParseSequence(seq, null), (l1, l2)        => select = ParseGroupBy   (l1, null, l2,   select, null)),
				pi => pi.IsQueryableMethod ("GroupBy", 1, 1, 2, seq => select = ParseSequence(seq, null), (l1, l2, l3)    => select = ParseGroupBy   (l1, l2,   l3,   select, null)),
				pi => pi.IsQueryableMethod ("Take",             seq => select = ParseSequence(seq, null), ex => ParseTake(select, ex)),
				pi => pi.IsQueryableMethod ("Skip",             seq => select = ParseSequence(seq, null), ex => ParseSkip(select, ex)),
				pi => pi.IsEnumerableMethod("DefaultIfEmpty",   seq => { select = ParseDefaultIfEmpty(parent, seq); return select != null; }),
				pi => pi.IsMethod(m =>
				{
					if (m.Expr.Method.DeclaringType == typeof(Queryable) || !TypeHelper.IsSameOrParent(typeof(IQueryable), pi.Expr.Type))
						return false;

					select = ParseSequence(GetIQueriable(info), null);
					return true;
				}),
				pi => { throw new ArgumentException(string.Format("Queryable method call expected. Got '{0}'.", pi.Expr), "info"); }
			);

			ParsingTracer.DecIndentLevel();
			ParsingTracer.WriteLine(select);
			return select;
		}

		QuerySource ParseTable(ParseInfo info)
		{
			if (info.NodeType == ExpressionType.MemberAccess)
			{
				if (_info.Parameters != null)
				{
					var me = (MemberExpression)info.Expr;

					if (me.Expression == _info.Parameters[0])
						return new QuerySource.Table(_info.MappingSchema, CurrentSql, new LambdaInfo(info));
				}
			}

			if (info.NodeType == ExpressionType.Call)
			{
				if (_info.Parameters != null)
				{
					var mc = (MethodCallExpression)info.Expr;

					if (mc.Object == _info.Parameters[0])
						return new QuerySource.Table(_info.MappingSchema, CurrentSql, new LambdaInfo(info));
				}
			}

			QuerySource select = null;

			if (info.IsConstant<IQueryable>((value,expr) =>
			{
				select = new QuerySource.Table(_info.MappingSchema, CurrentSql, new LambdaInfo(expr));
				return true;
			}))
			{}

			return select;
		}

		ParseInfo GetIQueriable(ParseInfo info)
		{
			if (info.NodeType == ExpressionType.MemberAccess || info.NodeType == ExpressionType.Call)
			{
				var p    = Expression.Parameter(typeof(Expression), "exp");
				var expr = ReplaceParameter(ParseInfo.CreateRoot(info.Expr, p), _ => {});
				var l    = Expression.Lambda<Func<Expression,IQueryable>>(Expression.Convert(expr, typeof(IQueryable)), new [] { p });
				var qe   = l.Compile();
				var n    = _info.AddQueryableAccessors(info.Expr, qe);

				return info.Create(
					qe(info).Expression,
					Expression.Call(
						_infoParam,
						Expressor<ExpressionInfo<T>>.MethodExpressor(a => a.GetIQueryable(0, null)),
						new [] { Expression.Constant(n), info.ParamAccessor }));
			}

			throw new InvalidOperationException();
		}

		#endregion

		#region Parse Select

		QuerySource ParseSelect(LambdaInfo l, params QuerySource[] sources)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

#if DEBUG
			try
			{
#endif
				for (var i = 0; i < sources.Length && i < l.Parameters.Length; i++)
					SetAlias(sources[i], l.Parameters[i].Expr.Name);

				switch (l.Body.NodeType)
				{
					case ExpressionType.Parameter  :
						for (var i = 0; i < sources.Length; i++)
							if (l.Body == l.Parameters[i].Expr)
								return sources[i];
						throw new InvalidOperationException();
					case ExpressionType.New        : return new QuerySource.Expr  (CurrentSql, l.ConvertTo<NewExpression>(),        sources);
					case ExpressionType.MemberInit : return new QuerySource.Expr  (CurrentSql, l.ConvertTo<MemberInitExpression>(), sources);
					default                        : return new QuerySource.Scalar(CurrentSql, l,                                   sources);
				}
#if DEBUG
			}
			finally
			{
				ParsingTracer.DecIndentLevel();
			}
#endif
		}

		#endregion

		#region Parse SelectMany

		QuerySource ParseSelectMany(LambdaInfo collectionSelector, LambdaInfo resultSelector, QuerySource source)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var sql = CurrentSql;

			CurrentSql = new SqlBuilder();

			var seq2 = ParseSequence(collectionSelector.Body, source);

			if (source.ParentQueries.Contains(seq2))
			{
				CurrentSql = sql;
				return resultSelector == null ? seq2 : ParseSelect(resultSelector, source, seq2);
			}

			var current = new SqlBuilder();
			var source1 = new QuerySource.SubQuery(current, sql,        source);
			var source2 = new QuerySource.SubQuery(current, CurrentSql, seq2);

			current.From.Table(source1.SubSql);
			current.From.Table(source2.SubSql);

			CurrentSql = current;

			var result = resultSelector == null ? source2 : ParseSelect(resultSelector, source1, source2);
			ParsingTracer.DecIndentLevel();
			return result;
		}

		#endregion

		#region Parse Join

		QuerySource ParseJoin(
			ParseInfo   inner,
			LambdaInfo  outerKeySelector,
			LambdaInfo  innerKeySelector,
			LambdaInfo  resultSelector,
			QuerySource outerSource)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			CheckExplicitCtor(outerKeySelector.Body);

			var current = new SqlBuilder();
			var source1 = new QuerySource.SubQuery(current, CurrentSql, outerSource);

			CurrentSql = new SqlBuilder();

			var seq     = ParseSequence(inner, null);
			var source2 = new QuerySource.SubQuery(current, CurrentSql, seq, false);
			var join    = source2.SubSql.InnerJoin();

			CurrentSql = current;

			current.From.Table(source1.SubSql, join);

			if (outerKeySelector.Body.NodeType == ExpressionType.New)
			{
				var new1 = outerKeySelector.Body.ConvertTo<NewExpression>();
				var new2 = innerKeySelector.Body.ConvertTo<NewExpression>();

				for (var i = 0; i < new1.Expr.Arguments.Count; i++)
					join
						.Expr(ParseExpression(source1, new1.Create(new1.Expr.Arguments[i], new1.Index(new1.Expr.Arguments, New.Arguments, i)))).Equal
						.Expr(ParseExpression(source2, new2.Create(new2.Expr.Arguments[i], new2.Index(new2.Expr.Arguments, New.Arguments, i))));
			}
			else
			{
				join
					.Expr(ParseExpression(source1, outerKeySelector.Body)).Equal
					.Expr(ParseExpression(source2, innerKeySelector.Body));
			}

			var result = resultSelector == null ? source2 : ParseSelect(resultSelector, source1, source2);
			ParsingTracer.DecIndentLevel();
			return result;
		}

		static void CheckExplicitCtor(Expression expr)
		{
			if (expr.NodeType == ExpressionType.MemberInit)
				throw new NotSupportedException(
					string.Format("Explicit construction of entity type '{0}' in query is not allowed.", expr.Type));
		}

		#endregion

		#region Parse GroupJoin

		QuerySource ParseGroupJoin(
			ParseInfo   inner,
			LambdaInfo  outerKeySelector,
			LambdaInfo  innerKeySelector,
			LambdaInfo  resultSelector,
			QuerySource outerSource)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			if (outerKeySelector.Body.NodeType == ExpressionType.MemberInit)
				throw new NotSupportedException(
					string.Format("Explicit construction of entity type '{0}' in query is not allowed.",
					outerKeySelector.Body.Expr.Type));

			// Process outer source.
			//
			var current = new SqlBuilder();
			var source1 = new QuerySource.SubQuery(current, CurrentSql, outerSource);

			// Process inner source.
			//
			CurrentSql = new SqlBuilder();

			var seq     = ParseSequence(inner, null);
			var source2 = new QuerySource.GroupJoinQuery(current, CurrentSql, seq);
			var join    = source2.SubSql.LeftJoin();

			CurrentSql = current;

			current.From.Table(source1.SubSql, join);

			// Process counter.
			//
			CurrentSql = new SqlBuilder();

			var cntseq   = ParseSequence(inner, null);
			var counter  = new QuerySource.SubQuery(current, CurrentSql, cntseq, false);
			var cntjoin  = counter.SubSql.WeakLeftJoin();
 
			CurrentSql = current;

			counter.SubSql.Select.Expr(new SqlFunction.Count(counter.SubSql.From.Tables[0]), "cnt");
			current.From.Table(source1.SubSql, cntjoin);

			// Make join and where for the counter.
			//
			if (outerKeySelector.Body.NodeType == ExpressionType.New)
			{
				var new1 = outerKeySelector.Body.ConvertTo<NewExpression>();
				var new2 = innerKeySelector.Body.ConvertTo<NewExpression>();

				for (var i = 0; i < new1.Expr.Arguments.Count; i++)
				{
					join
						.Expr(ParseExpression(source1, new1.Create(new1.Expr.Arguments[i], new1.Index(new1.Expr.Arguments, New.Arguments, i)))).Equal
						.Expr(ParseExpression(source2, new2.Create(new2.Expr.Arguments[i], new2.Index(new2.Expr.Arguments, New.Arguments, i))));

					//counter.SqlBuilder.Where
					cntjoin
						.Expr(ParseExpression(source1, new1.Create(new1.Expr.Arguments[i], new1.Index(new1.Expr.Arguments, New.Arguments, i)))).Equal
						.Expr(ParseExpression(counter, new2.Create(new2.Expr.Arguments[i], new2.Index(new2.Expr.Arguments, New.Arguments, i))));

					counter.SubSql.GroupBy
						.Expr(ParseExpression(cntseq, new2.Create(new2.Expr.Arguments[i], new2.Index(new2.Expr.Arguments, New.Arguments, i))));
				}
			}
			else
			{
				join
					.Expr(ParseExpression(source1, outerKeySelector.Body)).Equal
					.Expr(ParseExpression(source2, innerKeySelector.Body));

				cntjoin
					.Expr(ParseExpression(source1, outerKeySelector.Body)).Equal
					.Expr(ParseExpression(counter, innerKeySelector.Body));

				counter.SubSql.GroupBy
					.Expr(ParseExpression(cntseq, innerKeySelector.Body));
			}

			if (resultSelector == null)
				return source2;
			
			var select = ParseSelect(resultSelector, source1, source2, counter);

			source2.Counter = new QueryField.ExprColumn(select, counter.SubSql.Select.Columns[0], null);
			//source2.SourceInfo = inner;

			ParsingTracer.DecIndentLevel();
			return select;
		}

		#endregion

		#region Parse DefaultIfEmpty

		QuerySource ParseDefaultIfEmpty(QuerySource parent, ParseInfo<Expression> seq)
		{
			return parent.GetField(seq) as QuerySource;
		}

		#endregion

		#region Parse Where

		QuerySource ParseWhere(LambdaInfo l, QuerySource select)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			SetAlias(select, l.Parameters[0].Expr.Name);

			if (CheckSubQueryForWhere(select, l.Body))
				select = WrapInSubQuery(select);

			ParseSearchCondition(CurrentSql.Where.SearchCondition.Conditions, select, l.Body);

			ParsingTracer.DecIndentLevel();
			return select;
		}

		static bool CheckSubQueryForWhere(QuerySource query, ParseInfo expr)
		{
			var checkParameter = query is QuerySource.Scalar && query.Fields[0] is QueryField.ExprColumn;
			var makeSubQuery   = false;

			expr.Walk(pi =>
			{
				switch (pi.NodeType)
				{
					case ExpressionType.MemberAccess:
						{
							var field = query.GetField(pi.Expr);

							if (field is QueryField.ExprColumn)
								makeSubQuery = pi.StopWalking = true;

							break;
						}

					case ExpressionType.Parameter:
						if (checkParameter)
							makeSubQuery = pi.StopWalking = true;
						break;

				}

				return pi;
			});

			return makeSubQuery;
		}

		#endregion

		#region Parse GroupBy

		QuerySource ParseGroupBy(LambdaInfo keySelector, LambdaInfo elementSelector, LambdaInfo resultSelector, QuerySource source, Type groupingType)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			CheckExplicitCtor(keySelector.Body);

			var group   = ParseSelect(keySelector, source);
			var element = elementSelector != null? ParseSelect(elementSelector, source) : null;

			var byExprs = new ISqlExpression[group.Fields.Count];
			var wrap    = false;

			for (var i = 0; i < group.Fields.Count; i++)
			{
				var field = group.Fields[i];
				var exprs = field.GetExpressions(this);

				if (exprs == null || exprs.Length != 1)
					throw new LinqException("Cannot group by type '{0}'", keySelector.Body.Expr.Type);

				byExprs[i] = exprs[0];

				wrap = wrap || !(exprs[0] is SqlField || exprs[0] is SqlBuilder.Column);
			}

			if (wrap)
			{
				var subQuery = WrapInSubQuery(group);

				foreach (var field in group.Fields)
					CurrentSql.GroupBy.Expr(group.SqlBuilder.Select.Columns[field.Select(this)[0].Index]);

				group = subQuery;
			}
			else
				foreach (var field in byExprs)
					CurrentSql.GroupBy.Expr(field);

			var result =
				resultSelector == null ?
					new QuerySource.GroupBy(CurrentSql, group, source, keySelector, element, groupingType, wrap, byExprs) :
					ParseSelect(resultSelector, group, source);

			ParsingTracer.DecIndentLevel();
			return result;
		}

		#endregion

		#region Parse OrderBy

		QuerySource ParseOrderBy(LambdaInfo lambda, QuerySource source, bool isThen, bool ascending)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			CheckExplicitCtor(lambda.Body);

			if (CurrentSql.Select.TakeValue != null || CurrentSql.Select.SkipValue != null)
				source = WrapInSubQuery(source);

			var order = ParseSelect(lambda, source);

			if (!isThen)
				CurrentSql.OrderBy.Items.Clear();

			foreach (var field in order.Fields)
			{
				var exprs = field.GetExpressions(this);

				if (exprs == null)
					throw new LinqException("Cannot order by type '{0}'", lambda.Body.Expr.Type);

				foreach (var expr in exprs)
				{
					var e = expr;

					if (e is SqlBuilder.SearchCondition)
					{
						if (e.CanBeNull())
						{
							var notExpr = new SqlBuilder.SearchCondition
							{
								Conditions = { new SqlBuilder.Condition(true, new SqlBuilder.Predicate.Expr(expr, expr.Precedence)) }
							};

							e = Convert(new SqlFunction("CASE", expr, new SqlValue(1), notExpr, new SqlValue(0), new SqlValue(null)));
						}
						else
							e = Convert(new SqlFunction("CASE", expr, new SqlValue(1), new SqlValue(0)));
					}

					CurrentSql.OrderBy.Expr(e, !ascending);
				}
			}

			ParsingTracer.DecIndentLevel();
			return source;
		}

		#endregion

		#region Parse Take

		bool ParseTake(QuerySource select, ParseInfo<Expression> value)
		{
			if (value.Expr.Type != typeof(int))
				return false;

			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			CurrentSql.Select.Take(ParseExpression(select, value));

			_info.SqlProvider.SqlBuilder = CurrentSql;

			if (CurrentSql.Select.SkipValue != null && _info.SqlProvider.IsTakeSupported && !_info.SqlProvider.IsSkipSupported)
				CurrentSql.Select.Take(Convert(
					new SqlBinaryExpression(CurrentSql.Select.SkipValue, "+", CurrentSql.Select.TakeValue, typeof(int), Precedence.Additive)));

			if (!_info.SqlProvider.TakeAcceptsParameter)
			{
				var p = CurrentSql.Select.TakeValue as SqlParameter;

				if (p != null)
					p.IsQueryParameter = false;
			}

			ParsingTracer.DecIndentLevel();
			return true;
		}

		#endregion

		#region Parse Skip

		bool ParseSkip(QuerySource select, ParseInfo<Expression> value)
		{
			if (value.Expr.Type != typeof(int))
				return false;

			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var prevSkipValue = CurrentSql.Select.SkipValue;

			CurrentSql.Select.Skip(ParseExpression(select, value));

			_info.SqlProvider.SqlBuilder = CurrentSql;

			if (CurrentSql.Select.TakeValue != null)
			{
				if (_info.SqlProvider.IsSkipSupported || !_info.SqlProvider.IsTakeSupported)
					CurrentSql.Select.Take(Convert(
						new SqlBinaryExpression(CurrentSql.Select.TakeValue, "-", CurrentSql.Select.SkipValue, typeof (int), Precedence.Additive)));

				if (prevSkipValue != null)
					CurrentSql.Select.Skip(Convert(
						new SqlBinaryExpression(prevSkipValue, "+", CurrentSql.Select.SkipValue, typeof (int), Precedence.Additive)));
			}

			if (!_info.SqlProvider.TakeAcceptsParameter)
			{
				var p = CurrentSql.Select.SkipValue as SqlParameter;

				if (p != null)
					p.IsQueryParameter = false;
			}

			ParsingTracer.DecIndentLevel();
			return true;
		}

		#endregion

		#region Parse Aggregate

		interface IAggregateHelper
		{
			void SetAggregate(ExpressionParser<T> parser, ParseInfo pi);
		}

		class AggregateHelper<TE> : IAggregateHelper
		{
			public void SetAggregate(ExpressionParser<T> parser, ParseInfo pi)
			{
				var mapper = Expression.Lambda<ExpressionInfo<T>.Mapper<TE>>(
					pi, new[]
					{
						parser._infoParam,
						parser._contextParam,
						parser._dataReaderParam,
						parser._mapSchemaParam,
						parser._expressionParam,
						ParametersParam
					});

				parser._info.SetElementQuery(mapper.Compile());
			}
		}

		void ParseAggregate(ParseInfo<MethodCallExpression> parseInfo, LambdaInfo lambda, QuerySource select)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var idx =
				parseInfo.Expr.Method.Name == "Count" ?
					select.SqlBuilder.Select.Add(new SqlFunction.Count(select.SqlBuilder), "cnt"):
					select.SqlBuilder.Select.Add(new SqlFunction(parseInfo.Expr.Method.Name, ParseExpression(select, lambda.Body)));

			_buildSelect = () =>
			{
				var pi     = BuildField(parseInfo, new[] { idx });
				var helper = (IAggregateHelper)Activator.CreateInstance(typeof (AggregateHelper<>).MakeGenericType(typeof (T), parseInfo.Expr.Type));

				helper.SetAggregate(this, pi);
			};

			ParsingTracer.DecIndentLevel();
		}

		#endregion

		#region Build Select

		void SetQuery(QuerySource query, IndexConverter converter)
		{
			query.Select(this);
			_info.SetQuery();
		}

		void SetQuery(ParseInfo info)
		{
			var mapper = Expression.Lambda<ExpressionInfo<T>.Mapper<T>>(
				info, new[] { _infoParam, _contextParam, _dataReaderParam, _mapSchemaParam, _expressionParam, ParametersParam });

			_info.SetQuery(mapper.Compile());
		}

		void BuildSelect(QuerySource query, Action<QuerySource,IndexConverter> queryAction, Action<ParseInfo> newAction, IndexConverter converter)
		{
			ParsingTracer.WriteLine(query);
			ParsingTracer.IncIndentLevel();

			query.Match
			(
				table  => queryAction(table, converter),                                  // QueryInfo.Table
				expr   => BuildNew     (query, expr.Lambda.Body,   newAction),            // QueryInfo.Expr
				sub    => BuildSubQuery(sub,   queryAction,        newAction, converter), // QueryInfo.SubQuery
				scalar => BuildNew     (query, scalar.Lambda.Body, newAction),            // QueryInfo.Scalar
				group  => BuildGroupBy (group, group.Lambda.Body,  newAction)             // QueryInfo.GroupBy
			);

			ParsingTracer.DecIndentLevel();
		}

		void BuildNew(QuerySource query, ParseInfo expr, Action<ParseInfo> newAction)
		{
			ParsingTracer.WriteLine(expr);
			ParsingTracer.WriteLine(query);
			ParsingTracer.IncIndentLevel();

			var info = BuildNewExpression(query, expr, i => i);
			newAction(info);

			ParsingTracer.DecIndentLevel();
		}

		void BuildSubQuery(
			QuerySource.SubQuery               subQuery,
			Action<QuerySource,IndexConverter> queryAction,
			Action<ParseInfo>                  newAction,
			IndexConverter                     converter)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			subQuery.ParentQueries[0].Match
			(
				_      => queryAction(subQuery, converter), // QueryInfo.Table
				expr   => // QueryInfo.Expr
				{
					if (expr.Lambda.Body.NodeType == ExpressionType.New)
						newAction(BuildQuerySourceExpr(subQuery, expr.Lambda.Body, converter));
					else
						throw new NotImplementedException();
				}, 
				_      => { throw new NotImplementedException(); }, // QueryInfo.SubQuery
				scalar => // QueryInfo.Scalar
				{
					var idx  = subQuery.Fields[0].Select(this);
					var info = BuildField(scalar.Lambda.Body, idx.Select(i => converter(i).Index).ToArray());
					newAction(info);
				},
				_      => { throw new NotImplementedException(); }  // QueryInfo.GroupBy
			);

			ParsingTracer.DecIndentLevel();
		}

		ParseInfo BuildQuerySourceExpr(QuerySource query, ParseInfo parseInfo, IndexConverter converter)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			NewExpression newExpr = null;
			var           member  = 0;

			var result = parseInfo.Walk(pi =>
			{
				if (newExpr == null && pi.NodeType == ExpressionType.New)
				{
					newExpr = (NewExpression)pi.Expr;
				}
				else if (newExpr != null)
				{
					var mi = newExpr.Members[member++];

					if (mi is MethodInfo)
						mi = TypeHelper.GetPropertyByMethod((MethodInfo)mi);

					var field = query.GetField(mi);
					var idx   = field.Select(this);

					return BuildField(pi, idx.Select(i => converter(i).Index).ToArray());
				}

				return pi;
			});

			ParsingTracer.DecIndentLevel();
			return result;
		}

		#region BuildGroupBy

		interface IGroupByHelper
		{
			ParseInfo GetParseInfo(ExpressionParser<T> parser, QuerySource.GroupBy query, ParseInfo expr, Expression info);
		}

		class GroupByHelper<TKey,TElement,TSource> : IGroupByHelper
		{
			public ParseInfo GetParseInfo(ExpressionParser<T> parser, QuerySource.GroupBy query, ParseInfo expr, Expression info)
			{
				var valueParser = new ExpressionParser<TElement>();
				var keyParam    = Expression.Convert(Expression.ArrayIndex(ParametersParam, Expression.Constant(0)), typeof(TKey));

				Expression valueExpr = null;

				if (expr.NodeType == ExpressionType.New)
				{
					var ne = (NewExpression)expr.Expr;

					for (var i = 0; i < ne.Arguments.Count; i++)
					{
						var member = TypeHelper.GetPropertyByMethod((MethodInfo)ne.Members[i]);
						var equal  = Expression.Equal(ne.Arguments[i], Expression.MakeMemberAccess(keyParam, member));

						valueExpr = valueExpr == null ? equal : Expression.AndAlso(valueExpr, equal);
					}
				}
				else
				{
					valueExpr = Expression.Equal(query.Lambda.Body, keyParam);
				}

				valueExpr = Expression.Call(
					null,
					Expressor<object>.MethodExpressor(_ => Queryable.Where(null, (Expression<Func<TSource,bool>>)null)),
					new[]
					{
						query.OriginalQuery.Lambda.Body.Expr,
						Expression.Lambda<Func<TSource,bool>>(valueExpr, new[] { query.Lambda.Parameters[0].Expr })
					});

				if (query.ElementSource != null)
				{
					valueExpr = Expression.Call(
						null,
						Expressor<object>.MethodExpressor(_ => Queryable.Select(null, (Expression<Func<TSource,TElement>>)null)),
						new[]
						{
							valueExpr,
							Expression.Lambda<Func<TSource,TElement>>(query.ElementSource.Lambda.Body, new[] { query.ElementSource.Lambda.Parameters[0].Expr })
						});
				}

				var keyReader = Expression.Lambda<ExpressionInfo<T>.Mapper<TKey>>(
					info, new[]
					{
						parser._infoParam,
						parser._contextParam,
						parser._dataReaderParam,
						parser._mapSchemaParam,
						parser._expressionParam,
						ParametersParam
					});

				return expr.Parent.Replace(
					Expression.Call(parser._infoParam, parser._info.GetGroupingMethodInfo<TKey,TElement>(),
						parser._contextParam,
						parser._dataReaderParam,
						parser._expressionParam,
						ParametersParam,
						Expression.Constant(keyReader.Compile()),
						Expression.Constant(valueParser.Parse(parser._info.DataProvider, parser._info.MappingSchema, valueExpr, parser._info.Parameters))),
					expr.ParamAccessor);
			}
		}

		void BuildGroupBy(QuerySource.GroupBy query, ParseInfo expr, Action<ParseInfo> newAction)
		{
			ParsingTracer.WriteLine(expr);
			ParsingTracer.WriteLine(query);
			ParsingTracer.IncIndentLevel();

			ParseInfo info;

			if (query.IsWrapped && expr.NodeType != ExpressionType.New)
			{
				var idx = query.Fields[0].Select(this);

				if (idx.Length != 1)
					throw new InvalidOperationException();

				info = BuildField(expr, new[] { idx[0].Index });
			}
			else
				info = BuildNewExpression(query, expr, i => i);

			var args   = query.GroupingType.GetGenericArguments();
			var helper = (IGroupByHelper)Activator.CreateInstance(typeof(GroupByHelper<,,>).
				MakeGenericType(typeof(T), args[0], args[1], query.Lambda.Parameters[0].Expr.Type));

			info = helper.GetParseInfo(this, query, expr, info);
			newAction(info);

			ParsingTracer.DecIndentLevel();
		}

		#endregion

		ParseInfo BuildNewExpression(QuerySource query, ParseInfo expr, IndexConverter converter)
		{
			ParsingTracer.WriteLine(expr);
			ParsingTracer.WriteLine(query);
			ParsingTracer.IncIndentLevel();

			var newExpr = expr.Walk(pi =>
			{
				switch (pi.NodeType)
				{
					case ExpressionType.MemberAccess:
						{
							var ma = (ParseInfo<MemberExpression>)pi;

							if (IsServerSideOnly(pi))
								return BuildField(query, ma);

							var ex = pi.Create(ma.Expr.Expression, pi.Property(Member.Expression));

							if (query.ParentQueries.Length > 0)
							{
								var field = query.GetParentField(ma);

								if (field != null)
								{
									if (field is QueryField.Column || field is QuerySource.SubQuery)
										return BuildField(ma, field, converter/*i => i*/);

									if (field is QueryField.ExprColumn)
									{
										var col = (QueryField.ExprColumn)field;

										pi = BuildNewExpression(col.QuerySource, col.Expr, converter);
										pi.IsReplaced = pi.StopWalking = true;

										return pi;
									}

									if (field is QuerySource.Table)
										return BuildQueryField(ma, (QuerySource.Table)field, converter);

									if (field is QueryField.SubQueryColumn)
										return BuildSubQuery(ma, (QueryField.SubQueryColumn)field, converter);

									if (field is QueryField.GroupByColumn)
									{
										var ret = BuildGroupBy(ma, (QueryField.GroupByColumn)field, converter);

										ret.StopWalking = true;

										return ret;
									}

									throw new InvalidOperationException();
								}

								if (query is QuerySource.Scalar && ex.NodeType == ExpressionType.Constant)
									return BuildField(query, ma);
							}
							else
							{
								var field = query.GetField(ma);

								if (field != null)
									return BuildField(ma, field, converter/*i => i*/);
							}

							if (ex.NodeType == ExpressionType.Constant)
							{
								// field = localVariable
								//
								var c = ex.Parent.Create((ConstantExpression)ex.Expr, ex.Property<ConstantExpression>(Constant.Value));

								return pi.Parent.Replace(
									Expression.MakeMemberAccess(
										Expression.Convert(c.ParamAccessor, ex.Expr.Type),
										ma.Expr.Member),
									c.ParamAccessor);
							}

							break;
						}

					case ExpressionType.Parameter:
						{
							var field = query.GetParentField(pi.Expr);

							if (field != null)
							{
								if (field is QuerySource.Table)
									return BuildQueryField(pi, (QuerySource.Table)field, converter);

								if (field is QuerySource.Scalar)
								{
									var source = (QuerySource)field;
									return BuildNewExpression(source, source.Lambda.Body, converter);
								}

								if (field is QuerySource.Expr)
								{
									var source = (QuerySource)field;
									return BuildQuerySourceExpr(query, source.Lambda.Body, converter);
								}

								if (field is QuerySource.GroupJoinQuery)
									return BuildGroupJoin(pi, (QuerySource.GroupJoinQuery)field, converter);

								throw new InvalidOperationException();
							}

							break;
						}

					case ExpressionType.Constant:
						{
							if (query.ParentQueries.Length == 0)
							{
								var field = query.GetField(pi);

								if (field != null)
								{
									var idx = field.Select(this);
									return BuildField(pi, idx.Select(i => converter(i).Index).ToArray());
								}
							}

							if (query is QuerySource.Scalar)
								return BuildField(query, pi);

							break;
						}

					case ExpressionType.Coalesce:
					//case ExpressionType.Conditional:
						return BuildField(query.ParentQueries[0], pi);

					case ExpressionType.Call:
						{
							if (IsServerSideOnly(pi))
								return BuildField(query, pi);
						}

						break;

				}

				return pi;
			});

			ParsingTracer.DecIndentLevel();
			return newExpr;
		}

		ParseInfo BuildSubQuery(ParseInfo<MemberExpression> ma, QueryField.SubQueryColumn query, IndexConverter converter)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			if (query.Field is QuerySource.Table)
				return BuildQueryField(ma, (QuerySource.Table)query.Field, i => converter(query.QuerySource.EnsureField(i.Field).Select(this)[0]));

			if (query.Field is QuerySource)
				throw new InvalidOperationException();

			if (query.Field is QueryField.SubQueryColumn)
				return BuildSubQuery(ma, (QueryField.SubQueryColumn)query.Field, i => converter(query.QuerySource.EnsureField(i.Field).Select(this)[0]));

			var field = BuildField(ma, query, converter);

			ParsingTracer.DecIndentLevel();
			return field;
		}

		ParseInfo BuildQueryField(ParseInfo pi, QuerySource query, IndexConverter converter)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var index = query.Select(this).Select(i => converter(i).Index).ToArray();

			var field = pi.Parent.Replace(
				Expression.Convert(
					Expression.Call(_infoParam, _info.GetMapperMethodInfo(),
						Expression.Constant(pi.Expr.Type),
						_dataReaderParam,
						Expression.Constant(_info.GetMapperSlot(index))),
					pi.Expr.Type),
				pi.ParamAccessor);

			ParsingTracer.DecIndentLevel();
			return field;
		}

		#region BuildGroupJoin

		interface IGroupJoinHelper
		{
			ParseInfo GetParseInfo(ExpressionParser<T> parser, ParseInfo ma, FieldIndex counterIndex, Expression info);
		}

		class GroupJoinHelper<TE> : IGroupJoinHelper
		{
			public ParseInfo GetParseInfo(ExpressionParser<T> parser, ParseInfo ma, FieldIndex counterIndex, Expression info)
			{
				var itemReader = Expression.Lambda<ExpressionInfo<T>.Mapper<TE>>(
					info, new[]
					{
						parser._infoParam,
						parser._contextParam,
						parser._dataReaderParam,
						parser._mapSchemaParam,
						parser._expressionParam,
						ParametersParam
					});

				return ma.Parent.Replace(
					Expression.Call(parser._infoParam, parser._info.GetGroupJoinEnumeratorMethodInfo<TE>(),
						parser._contextParam,
						parser._dataReaderParam,
						parser._expressionParam,
						ParametersParam,
						Expression.Constant(counterIndex.Index),
						Expression.Constant(itemReader.Compile())),
					ma.ParamAccessor);
			}
		}

		ParseInfo BuildGroupJoin(ParseInfo ma, QuerySource.GroupJoinQuery query, IndexConverter converter)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var args = ma.Expr.Type.GetGenericArguments();

			Expression expr = null;

			BuildSelect(
				query.ParentQueries[0],
				(q, c) =>
				{
					var index = q.Select(this).Select(i => c(i).Index).ToArray();

					expr = Expression.Convert(
						Expression.Call(_infoParam, _info.GetMapperMethodInfo(),
							Expression.Constant(args[0]),
							_dataReaderParam,
							Expression.Constant(_info.GetMapperSlot(index))),
						args[0]);
				},
				info  => expr = info,
				i     => converter(query.EnsureField(i.Field).Select(this)[0]));

			var helper       = (IGroupJoinHelper)Activator.CreateInstance(typeof(GroupJoinHelper<>).MakeGenericType(typeof(T), args[0]));
			var counterIndex = converter(query.Counter.Select(this)[0]);

			var result       = helper.GetParseInfo(this, ma, counterIndex, expr);
			ParsingTracer.DecIndentLevel();
			return result;
		}

		#endregion

		ParseInfo BuildGroupBy(ParseInfo<MemberExpression> ma, QueryField.GroupByColumn field, IndexConverter converter)
		{
			ParsingTracer.WriteLine(ma);
			ParsingTracer.WriteLine(field);
			ParsingTracer.IncIndentLevel();

			var source = field.GroupBySource.ParentQueries[0];

			if (source is QuerySource.Scalar)
				return BuildField(ma, field.GroupBySource, converter);

			if (source is QuerySource.SubQuery)
			{
				if (source.ParentQueries[0] is QuerySource.Scalar)
					return BuildField(ma, field.GroupBySource, converter);

				return BuildNewExpression(source, field.GroupBySource.Lambda.Body, converter /*i => converter(source.EnsureField(i.Field).Select(this)[0])*/);
			}

			var result = BuildNewExpression(source, field.GroupBySource.Lambda.Body, converter);
			ParsingTracer.DecIndentLevel();
			return result;
		}

		ParseInfo BuildField(QuerySource query, ParseInfo pi)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			var sqlex = ParseExpression(query, pi);
			var idx   = CurrentSql.Select.Add(sqlex);
			var field = BuildField(pi, new[] { idx });

			ParsingTracer.IncIndentLevel();
			return field;
		}

		ParseInfo BuildField(ParseInfo<MemberExpression> ma, QueryField field, IndexConverter converter)
		{
			ParsingTracer.WriteLine(ma);
			ParsingTracer.WriteLine(field);
			ParsingTracer.IncIndentLevel();

			ParseInfo result = null;

			if (field is QuerySource.SubQuery)
			{
				var query = (QuerySource.SubQuery)field;

				if (query is QuerySource.GroupJoinQuery && TypeHelper.IsSameOrParent(typeof(IEnumerable), ma.Expr.Type))
					result = BuildGroupJoin(ma, (QuerySource.GroupJoinQuery)query, converter);
				else if (query.Sources[0] is QuerySource.Table)
				{
					var table = (QuerySource.Table)query.Sources[0];

					if (ma.Expr.Type == table.ObjectType)
						result = BuildQueryField(ma, table, i => converter(query.EnsureField(i.Field).Select(this)[0]));
				}
			}

			if (result == null)
			{
				var idx = field.Select(this);
				result = BuildField(ma, idx.Select(i => converter(i).Index).ToArray());
			}

			ParsingTracer.DecIndentLevel();
			return result;
		}

		ParseInfo BuildField(ParseInfo ma, int[] idx)
		{
			ParsingTracer.WriteLine();
			ParsingTracer.IncIndentLevel();

			if (idx.Length != 1)
				throw new InvalidOperationException();

			MethodInfo mi;

			if (!MapSchema.Converters.TryGetValue(ma.Expr.Type, out mi))
				throw new LinqException("Cannot find converter for the '{0}' type.", ma.Expr.Type.FullName);

			var mapper = Expression.Call(_mapSchemaParam, mi, Expression.Call(_dataReaderParam, DataReader.GetValue, Expression.Constant(idx[0])));
			var result = ma.Parent == null ? ma.Create(mapper, ma.ParamAccessor) : ma.Parent.Replace(mapper, ma.ParamAccessor);

			ParsingTracer.DecIndentLevel();
			return result;
		}

		static void SetAlias(QuerySource query, string alias)
		{
			if (alias.Contains('<'))
				return;

			query.Match
			(
				table  =>
				{
					if (table.SqlTable.Alias == null)
						table.SqlTable.Alias = alias;
				},
				_ => {},
				subQuery =>
				{
					var table = subQuery.SqlBuilder.From.Tables[0];
					if (table.Alias == null)
						table.Alias = alias;
				},
				_ => {},
				_ => {}
			);
		}

		bool SimpleQuery(Type type, string alias)
		{
			var table = new SqlTable(_info.MappingSchema, type) { Alias = alias };

			foreach (var field in table.Fields.Values)
				CurrentSql.Select.Expr(field);

			CurrentSql.From.Table(table);
			_info.SetQuery();

			return true;
		}

		#endregion

		#region Build Scalar Select

		void BuildScalarSelect(ParseInfo parseInfo)
		{
			switch (parseInfo.NodeType)
			{
				case ExpressionType.New:
				case ExpressionType.MemberInit:
					BuildNew(ParseSelect(new LambdaInfo(parseInfo)), parseInfo, SetQuery);
					return;
			}

			var expr = ParseExpression(null, parseInfo);

			CurrentSql.Select.Expr(expr);

			var pi = BuildField(parseInfo, new[] { 0 });

			var mapper = Expression.Lambda<ExpressionInfo<T>.Mapper<T>>(
				pi, new [] { _infoParam, _contextParam, _dataReaderParam, _mapSchemaParam, _expressionParam, ParametersParam });

			_info.SetQuery(mapper.Compile());
		}

		#endregion

		#region Build Constant

		readonly Dictionary<Expression,SqlValue> _constants = new Dictionary<Expression,SqlValue>();

		SqlValue BuildConstant(ParseInfo expr)
		{
			SqlValue value;

			if (_constants.TryGetValue(expr.Expr, out value))
				return value;

			var lambda = Expression.Lambda<Func<object>>(Expression.Convert(expr,typeof(object)));

			value = new SqlValue(lambda.Compile()());

			_constants.Add(expr.Expr, value);

			return value;
		}

		#endregion

		#region Build Parameter

		readonly Dictionary<Expression,ExpressionInfo<T>.Parameter> _parameters = new Dictionary<Expression, ExpressionInfo<T>.Parameter>();
		readonly Dictionary<Expression,Expression>                  _accessors  = new Dictionary<Expression, Expression>();

		ExpressionInfo<T>.Parameter BuildParameter(ParseInfo expr)
		{
			ExpressionInfo<T>.Parameter p;

			if (_parameters.TryGetValue(expr.Expr, out p))
				return p;

			string name = null;

			var newExpr = ReplaceParameter(expr, nm => name = nm);
			var mapper  = Expression.Lambda<Func<ExpressionInfo<T>,Expression,object[],object>>(
				Expression.Convert(newExpr, typeof(object)),
				new [] { _infoParam, _expressionParam, ParametersParam });

			p = new ExpressionInfo<T>.Parameter
			{
				Expression   = expr.Expr,
				Accessor     = mapper.Compile(),
				SqlParameter = new SqlParameter(name, null)
			};

			_parameters.Add(expr.Expr, p);
			CurrentSqlParameters.Add(p);

			return p;
		}

		ParseInfo ReplaceParameter(ParseInfo expr, Action<string> setName)
		{
			return expr.Walk(pi =>
			{
				if (pi.NodeType == ExpressionType.MemberAccess)
				{
					Expression accessor;

					if (_accessors.TryGetValue(pi.Expr, out accessor))
					{
						var ma = (MemberExpression)pi.Expr;
						setName(ma.Member.Name);

						return pi.Parent.Replace(pi.Expr, accessor);
					}
				}

				pi.IsConstant(c =>
				{
					if (!TypeHelper.IsScalar(pi.Expr.Type))
					{
						var e = Expression.Convert(c.ParamAccessor, pi.Expr.Type);
						pi = pi.Parent.Replace(e, c.ParamAccessor);

						if (pi.Parent.NodeType == ExpressionType.MemberAccess)
						{
							var ma = (MemberExpression)pi.Parent.Expr;
							setName(ma.Member.Name);
						}
					}

					return true;
				});

				return pi;
			});
		}

		#endregion

		#region Expression Parser

		ISqlExpression Convert(ISqlExpression expr)
		{
			return _info.SqlProvider.ConvertExpression(expr);
		}

		ISqlPredicate Convert(ISqlPredicate predicate)
		{
			return _info.SqlProvider.ConvertPredicate(predicate);
		}

		public ISqlExpression ParseExpression(QuerySource query, ParseInfo parseInfo)
		{
			if (parseInfo.NodeType == ExpressionType.Parameter && query is QuerySource.Scalar)
			{
				var ma = (QuerySource.Scalar)query;
				return ParseExpression(ma.ParentQueries[0], ma.Lambda.Body);
			}

			if (CanBeConstant(parseInfo))
				return BuildConstant(parseInfo);

			if (CanBeCompiled(parseInfo))
				return BuildParameter(parseInfo).SqlParameter;

			switch (parseInfo.NodeType)
			{
				case ExpressionType.AndAlso:
				case ExpressionType.OrElse:
				case ExpressionType.Not:
				case ExpressionType.Equal:
				case ExpressionType.NotEqual:
				case ExpressionType.GreaterThan:
				case ExpressionType.GreaterThanOrEqual:
				case ExpressionType.LessThan:
				case ExpressionType.LessThanOrEqual:
					{
						var condition = new SqlBuilder.SearchCondition();
						ParseSearchCondition(condition.Conditions, query, parseInfo);
						return condition;
					}

				case ExpressionType.Add:
				case ExpressionType.AddChecked:
				case ExpressionType.And:
				case ExpressionType.Divide:
				case ExpressionType.ExclusiveOr:
				case ExpressionType.Modulo:
				case ExpressionType.Multiply:
				case ExpressionType.Or:
				case ExpressionType.Power:
				case ExpressionType.Subtract:
				case ExpressionType.SubtractChecked:
				case ExpressionType.Coalesce:
					{
						var pi = parseInfo.Convert<BinaryExpression>();
						var e  = parseInfo.Expr as BinaryExpression;
						var l  = ParseExpression(query, pi.Create(e.Left,  pi.Property(Binary.Left)));
						var r  = ParseExpression(query, pi.Create(e.Right, pi.Property(Binary.Right)));
						var t  = e.Left.Type ?? e.Right.Type;

						switch (parseInfo.NodeType)
						{
							case ExpressionType.Add            :
							case ExpressionType.AddChecked     : return Convert(new SqlBinaryExpression(l, "+", r, t, Precedence.Additive));
							case ExpressionType.And            : return Convert(new SqlBinaryExpression(l, "&", r, t, Precedence.Bitwise));
							case ExpressionType.Divide         : return Convert(new SqlBinaryExpression(l, "/", r, t, Precedence.Multiplicative));
							case ExpressionType.ExclusiveOr    : return Convert(new SqlBinaryExpression(l, "^", r, t, Precedence.Bitwise));
							case ExpressionType.Modulo         : return Convert(new SqlBinaryExpression(l, "%", r, t, Precedence.Multiplicative));
							case ExpressionType.Multiply       : return Convert(new SqlBinaryExpression(l, "*", r, t, Precedence.Multiplicative));
							case ExpressionType.Or             : return Convert(new SqlBinaryExpression(l, "|", r, t, Precedence.Bitwise));
							case ExpressionType.Power          : return Convert(new SqlFunction("Power", l, r));
							case ExpressionType.Subtract       :
							case ExpressionType.SubtractChecked: return Convert(new SqlBinaryExpression(l, "-", r, t, Precedence.Subtraction));
							case ExpressionType.Coalesce       :
								{
									if (r is SqlFunction)
									{
										var c = (SqlFunction)r;

										if (c.Name == "Coalesce")
										{
											var parms = new ISqlExpression[c.Parameters.Length + 1];

											parms[0] = l;
											c.Parameters.CopyTo(parms, 1);

											return Convert(new SqlFunction("Coalesce", parms));
										}
									}

									return Convert(new SqlFunction("Coalesce", l, r));
								}
						}

						break;
					}

				case ExpressionType.Convert:
					{
						var pi = parseInfo.Convert<UnaryExpression>();
						var e  = parseInfo.Expr as UnaryExpression;
						var o  = ParseExpression(query, pi.Create(e.Operand, pi.Property(Unary.Operand)));

						if (e.Method == null && e.IsLifted)
							return o;

						return Convert(new SqlFunction("$Convert$", new SqlDataType(e.Type), new SqlDataType(e.Operand.Type), o));
					}

				case ExpressionType.Conditional:
					{
						var pi = parseInfo.Convert<ConditionalExpression>();
						var e  = parseInfo.Expr as ConditionalExpression;
						var s  = ParseExpression(query, pi.Create(e.Test,    pi.Property(Conditional.Test)));
						var t  = ParseExpression(query, pi.Create(e.IfTrue,  pi.Property(Conditional.IfTrue)));
						var f  = ParseExpression(query, pi.Create(e.IfFalse, pi.Property(Conditional.IfFalse)));

						if (f is SqlFunction)
						{
							var c = (SqlFunction)f;

							if (c.Name == "CASE")
							{
								var parms = new ISqlExpression[c.Parameters.Length + 2];

								parms[0] = s;
								parms[1] = t;
								c.Parameters.CopyTo(parms, 2);

								return Convert(new SqlFunction("CASE", parms));
							}
						}

						return Convert(new SqlFunction("CASE", s, t, f));
					}

				case ExpressionType.MemberAccess:
					{
						var ma = (MemberExpression)parseInfo.Expr;
						var ef = _info.SqlProvider.ConvertMember(ma.Member);

						if (ef != null)
						{
							var pi = parseInfo.ConvertTo<MemberExpression>();

							var pie = parseInfo.Parent.Replace(ef, null).Walk(wpi =>
							{
								if (wpi.NodeType == ExpressionType.Parameter)
								{
									var expr = ma.Expression;

									if (expr.NodeType == ExpressionType.MemberAccess)
										if (!_accessors.ContainsKey(expr))
											_accessors.Add(expr, pi.Property(Member.Expression));

									return pi.Create(expr, null);
								}

								return wpi;
							});

							return ParseExpression(query, pie);
						}

						var attrs = ma.Member.GetCustomAttributes(typeof(SqlPropertyAttribute), true);

						if (attrs.Length > 0)
						{
							var attr = (SqlPropertyAttribute)attrs[0];
							return Convert(new SqlExpression(attr.Name ?? ma.Member.Name));
						}

						goto case ExpressionType.Parameter;
					}

				case ExpressionType.Parameter:
					{
						var field = query.GetField(parseInfo.Expr);

						if (field != null)
						{
							var exprs = field.GetExpressions(this);

							if (exprs == null)
								break;

							if (exprs.Length == 1)
								return exprs[0];

							throw new InvalidOperationException();
						}

						break;
					}

				case ExpressionType.Call:
					{
						var pi = parseInfo.Convert<MethodCallExpression>();
						var e  = parseInfo.Expr as MethodCallExpression;

						if (e.Method.DeclaringType == typeof(Enumerable))
							return ParseEnumerable(query, pi);

						var ef = _info.SqlProvider.ConvertMember(e.Method);

						if (ef != null)
						{
							var pie = parseInfo.Parent.Replace(ef, null).Walk(wpi =>
							{
								if (wpi.NodeType == ExpressionType.Parameter)
								{
									Expression       expr;
									Func<Expression> fparam;

									var pe = (ParameterExpression)wpi.Expr;

									if (pe.Name == "obj")
									{
										expr   = e.Object;
										fparam = () => pi.Property(MethodCall.Object);
									}
									else
									{
										var i  = int.Parse(pe.Name.Substring(1));
										expr   = e.Arguments[i];
										fparam = () => pi.Index(e.Arguments, MethodCall.Arguments, i);
									}

									if (expr.NodeType == ExpressionType.MemberAccess)
										if (!_accessors.ContainsKey(expr))
											_accessors.Add(expr, fparam());

									return pi.Create(expr, null);
								}

								return wpi;
							});

							return ParseExpression(query, pie);
						}

						var attr = GetFunctionAttribute(e.Method);

						if (attr != null)
						{
							if (attr is SqlPropertyAttribute)
								return Convert(new SqlExpression(attr.Name ?? e.Method.Name));

							var parms = new List<ISqlExpression>();

							if (e.Object != null)
								parms.Add(ParseExpression(query, pi.Create(e.Object, pi.Property(MethodCall.Object))));

							for (var i = 0; i < e.Arguments.Count; i++)
								parms.Add(ParseExpression(query, pi.Create(e.Arguments[i], pi.Index(e.Arguments, MethodCall.Arguments, i))));

							return Convert(new SqlFunction(attr.Name ?? e.Method.Name, parms.ToArray()));
						}

						break;
					}
			}

			throw new LinqException("'{0}' cannot be converted to SQL.", parseInfo.Expr);
		}

		ISqlExpression ParseEnumerable(QuerySource query, ParseInfo<Expression> pi)
		{
			if (!(query.ParentQueries[0] is QuerySource.GroupBy))
				throw new LinqException("'{0}' cannot be converted to SQL.", pi.Expr);

			var groupBy = ((QuerySource.GroupBy)query.ParentQueries[0]).OriginalQuery;

			var expr = (MethodCallExpression)pi.Expr;
			var args = new ISqlExpression[expr.Arguments.Count - 1];

			if (expr.Method.Name == "Count")
			{
				if (args.Length > 0)
				{
					var predicate = ParsePredicate(groupBy, ParseLambdaArgument(pi, 1));

					groupBy.SqlBuilder.Where.SearchCondition.Conditions.Add(new SqlBuilder.Condition(false, predicate));

					var sql = groupBy.SqlBuilder.Clone(o => !(o is SqlParameter));

					groupBy.SqlBuilder.Where.SearchCondition.Conditions.RemoveAt(groupBy.SqlBuilder.Where.SearchCondition.Conditions.Count - 1);

					sql.Select.Columns.RemoveAll(_ => true);

					if (_info.SqlProvider.IsSubQueryColumnSupported && _info.SqlProvider.IsCountSubQuerySupported)
					{
						for (var i = 0; i < sql.GroupBy.Items.Count; i++)
						{
							var item1 = sql.GroupBy.Items[i];
							var item2 = groupBy.SqlBuilder.GroupBy.Items[i];

							if (item1.CanBeNull() && item2.CanBeNull())
								sql.Where
									.Expr(item1).IsNull.    And .Expr(item2).IsNull. Or
									.Expr(item1).IsNotNull. And .Expr(item2).IsNotNull. And .Expr(item1).Equal.Expr(item2);
							else
								sql.Where.Expr(item1).Equal.Expr(item2);
						}

						sql.GroupBy.Items.RemoveAll(_ => true);
						sql.Select.Expr(new SqlFunction.Count(sql));
						sql.ParentSql = groupBy.SqlBuilder;

						return sql;
					}

					var join = sql.WeakLeftJoin();

					groupBy.SqlBuilder.From.Tables[0].Joins.Add(join.JoinedTable);

					for (var i = 0; i < sql.GroupBy.Items.Count; i++)
					{
						var item1 = sql.GroupBy.Items[i];
						var item2 = groupBy.SqlBuilder.GroupBy.Items[i];
						var col   = sql.Select.Columns[sql.Select.Add(item1)];

						if (item1.CanBeNull() && item2.CanBeNull())
							join
								.Expr(col).IsNull.    And .Expr(item2).IsNull. Or
								.Expr(col).IsNotNull. And .Expr(item2).IsNotNull. And .Expr(col).Equal.Expr(item2);
						else
							join
								.Expr(col).Equal.Expr(item2);
					}

					sql.ParentSql = groupBy.SqlBuilder;

					return new SqlFunction("Count", sql.Select.Columns[0]);
				}

				return new SqlFunction.Count(groupBy.SqlBuilder);
			}

			for (var i = 1; i < expr.Arguments.Count; i++)
				args[i - 1] = ParseExpression(groupBy, ParseLambdaArgument(pi, i));

			return new SqlFunction(expr.Method.Name, args);
		}

		ParseInfo ParseLambdaArgument(ParseInfo<Expression> pi, int idx)
		{
			var expr = (MethodCallExpression)pi.Expr;
			var arg  = pi.Create(expr.Arguments[idx], pi.Index(expr.Arguments, MethodCall.Arguments, idx));
			
			arg.IsLambda<Expression>(new Func<ParseInfo<ParameterExpression>,bool>[]
				{ _ => true },
				body => { arg = body; return true; },
				_ => true);

			return arg;
		}

		bool IsServerSideOnly(ParseInfo parseInfo)
		{
			var isServerSideOnly = false;

			parseInfo.Walk(pi =>
			{
				if (isServerSideOnly)
					return pi;

				switch (pi.NodeType)
				{
					case ExpressionType.MemberAccess:
						{
							var ma = (MemberExpression)pi.Expr;
							var ef = _info.SqlProvider.ConvertMember(ma.Member);

							if (ef != null)
							{
								isServerSideOnly = IsServerSideOnly(pi.Parent.Replace(ef, null));
								break;
							}

							var attr = GetFunctionAttribute(ma.Member);

							isServerSideOnly = attr != null && attr.ServerSideOnly;
						}

						break;

					case ExpressionType.Call:
						{
							var e  = pi.Expr as MethodCallExpression;

							if (e.Method.DeclaringType == typeof(Enumerable))
							{
								switch (e.Method.Name)
								{
									case "Count":
									case "Average":
									case "Min":
									case "Max":
									case "Sum":
										isServerSideOnly = true;
										break;
								}
							}
							else
							{
								var ef = _info.SqlProvider.ConvertMember(e.Method);

								if (ef != null)
								{
									isServerSideOnly = IsServerSideOnly(pi.Parent.Replace(ef, null));
									break;
								}

								var attr = GetFunctionAttribute(e.Method);

								isServerSideOnly = attr != null && attr.ServerSideOnly;
							}
						}

						break;
				}

				return pi;
			});

			return isServerSideOnly;
		}

		bool CanBeConstant(ParseInfo expr)
		{
			var canbe = true;

			expr.Walk(pi =>
			{
				var ex = pi.Expr;

				if (ex is BinaryExpression || ex is UnaryExpression || ex.NodeType == ExpressionType.Convert)
					return pi;

				switch (ex.NodeType)
				{
					case ExpressionType.Constant:
						{
							var c = (ConstantExpression)ex;

							if (c.Value == null || IsConstant(ex.Type))
								return pi;

							break;
						}

					case ExpressionType.MemberAccess:
						{
							var ma = (MemberExpression)ex;

							if (IsConstant(ma.Member.DeclaringType))
								return pi;

							break;
						}

					case ExpressionType.Call:
						{
							var mc = (MethodCallExpression)ex;

							if (IsConstant(mc.Method.DeclaringType) || mc.Method.DeclaringType == typeof(object))
								return pi;

							var attr = GetFunctionAttribute(mc.Method);

							if (attr != null && !attr.ServerSideOnly)
								return pi;

							break;
						}
				}

				canbe = false;
				pi.StopWalking = true;

				return pi;
			});

			return canbe;
		}

		bool CanBeCompiled(ParseInfo expr)
		{
			var canbe = true;

			expr.Walk(pi =>
			{
				if (canbe)
				{
					canbe = !IsServerSideOnly(pi);

					if (canbe) switch (pi.NodeType)
					{
						case ExpressionType.Parameter:
							{
								var p = (ParameterExpression)pi.Expr;

								canbe = p == ParametersParam;
								break;
							}

						case ExpressionType.MemberAccess:
							{
								var ma   = (MemberExpression)pi.Expr;
								var attr = GetFunctionAttribute(ma.Member);

								canbe = attr == null  || !attr.ServerSideOnly;
								break;
							}

						case ExpressionType.Call:
							{
								var mc   = (MethodCallExpression)pi.Expr;
								var attr = GetFunctionAttribute(mc.Method);

								canbe = attr == null  || !attr.ServerSideOnly;
								break;
							}
					}
				}

				pi.StopWalking = !canbe;

				return pi;
			});

			return canbe;
		}

		public static bool IsConstant(Type type)
		{
			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Int16   :
				case TypeCode.Int32   :
				case TypeCode.Int64   :
				case TypeCode.UInt16  :
				case TypeCode.UInt32  :
				case TypeCode.UInt64  :
				case TypeCode.SByte   :
				case TypeCode.Byte    :
				case TypeCode.Boolean :
				case TypeCode.String  :
				case TypeCode.Char    : return true;
				default               : return false;
			}
		}

		#endregion

		#region Predicate Parser

		ISqlPredicate ParsePredicate(QuerySource query, ParseInfo parseInfo)
		{
			switch (parseInfo.NodeType)
			{
				case ExpressionType.Equal:
				case ExpressionType.NotEqual:
				case ExpressionType.GreaterThan:
				case ExpressionType.GreaterThanOrEqual:
				case ExpressionType.LessThan:
				case ExpressionType.LessThanOrEqual:
					{
						var pi = parseInfo.Convert<BinaryExpression>();
						var e  = parseInfo.Expr as BinaryExpression;
						var l  = ParseExpression(query, pi.Create(e.Left,  pi.Property(Binary.Left)));
						var r  = ParseExpression(query, pi.Create(e.Right, pi.Property(Binary.Right)));

						SqlBuilder.Predicate.Operator op;

						switch (parseInfo.NodeType)
						{
							case ExpressionType.Equal   :
							case ExpressionType.NotEqual:

								if (!CurrentSql.ParameterDependent && (l is SqlParameter && r.CanBeNull() || r is SqlParameter && l.CanBeNull()))
									CurrentSql.ParameterDependent = true;

								break;
						}

						switch (parseInfo.NodeType)
						{
							case ExpressionType.Equal             : op = SqlBuilder.Predicate.Operator.Equal;          break;
							case ExpressionType.NotEqual          : op = SqlBuilder.Predicate.Operator.NotEqual;       break;
							case ExpressionType.GreaterThan       : op = SqlBuilder.Predicate.Operator.Greater;        break;
							case ExpressionType.GreaterThanOrEqual: op = SqlBuilder.Predicate.Operator.GreaterOrEqual; break;
							case ExpressionType.LessThan          : op = SqlBuilder.Predicate.Operator.Less;           break;
							case ExpressionType.LessThanOrEqual   : op = SqlBuilder.Predicate.Operator.LessOrEqual;    break;
							default: throw new InvalidOperationException();
						}

						return Convert(new SqlBuilder.Predicate.ExprExpr(l, op, r));
					}

				case ExpressionType.Call:
					{
						var pi = parseInfo.Convert<MethodCallExpression>();
						var e  = pi.Expr as MethodCallExpression;

						ISqlPredicate predicate = null;

						if      (e.Method == Functions.String.Contains)   predicate = BuildLikePredicate(query, pi, "%", "%");
						else if (e.Method == Functions.String.StartsWith) predicate = BuildLikePredicate(query, pi, "",  "%");
						else if (e.Method == Functions.String.EndsWith)   predicate = BuildLikePredicate(query, pi, "%", "");
						else if (e.Method == Functions.String.Like11)     predicate = BuildLikePredicate(query, pi);
						else if (e.Method == Functions.String.Like12)     predicate = BuildLikePredicate(query, pi);
						else if (e.Method == Functions.String.Like21)     predicate = BuildLikePredicate(query, pi);
						else if (e.Method == Functions.String.Like22)     predicate = BuildLikePredicate(query, pi);

						if (predicate != null)
							return Convert(predicate);

						break;
					}

				case ExpressionType.Conditional:
					return Convert(new SqlBuilder.Predicate.ExprExpr(
						ParseExpression(query, parseInfo),
						SqlBuilder.Predicate.Operator.Equal,
						new SqlValue(true)));

				case ExpressionType.MemberAccess:
					{
						var pi = parseInfo.Convert<MemberExpression>();
						var e  = pi.Expr as MemberExpression;

						if (e.Member.Name == "HasValue" && e.Member.DeclaringType.GetGenericTypeDefinition() == typeof(Nullable<>))
						{
							var expr = ParseExpression(query, pi.Create(e.Expression, pi.Property(Member.Expression)));
							return Convert(new SqlBuilder.Predicate.IsNull(expr, true));
						}

						break;
					}
			}

			throw new InvalidOperationException();
		}

		#region LIKE predicate

		private ISqlPredicate BuildLikePredicate(QuerySource query, ParseInfo pi, string start, string end)
		{
			var e  = pi.Expr as MethodCallExpression;

			var o = ParseExpression(query, pi.Create(e.Object,       pi.Property(MethodCall.Object)));
			var a = ParseExpression(query, pi.Create(e.Arguments[0], pi.Index(e.Arguments, MethodCall.Arguments, 0)));

			if (a is SqlValue)
			{
				var value = ((SqlValue)a).Value;

				if (value == null)
					throw new LinqException("NULL cannot be used as a LIKE predicate parameter.");

				return value.ToString().IndexOfAny(new[] { '%', '_' }) < 0?
					new SqlBuilder.Predicate.Like(o, false, new SqlValue(start + value + end), null):
					new SqlBuilder.Predicate.Like(o, false, new SqlValue(start + EscapeLikeText(value.ToString()) + end), new SqlValue('~'));
			}

			if (a is SqlParameter)
			{
				var p  = (SqlParameter)a;
				var ep = (from pm in CurrentSqlParameters where pm.SqlParameter == p select pm).First();

				ep = new ExpressionInfo<T>.Parameter
				{
					Expression   = ep.Expression,
					Accessor     = ep.Accessor,
					SqlParameter = new SqlParameter(p.Name, p.Value, GetLikeEscaper(start, end))
				};

				_parameters.Add(e, ep);
				CurrentSqlParameters.Add(ep);

				return new SqlBuilder.Predicate.Like(o, false, ep.SqlParameter, new SqlValue('~'));
			}

			return null;
		}

		private ISqlPredicate BuildLikePredicate(QuerySource query, ParseInfo pi)
		{
			var e  = pi.Expr as MethodCallExpression;
			var a1 = ParseExpression(query, pi.Create(e.Arguments[0], pi.Index(e.Arguments, MethodCall.Arguments, 0)));
			var a2 = ParseExpression(query, pi.Create(e.Arguments[1], pi.Index(e.Arguments, MethodCall.Arguments, 1)));

			ISqlExpression a3 = null;

			if (e.Arguments.Count == 3)
				a3 = ParseExpression(query, pi.Create(e.Arguments[2], pi.Index(e.Arguments, MethodCall.Arguments, 2)));

			return new SqlBuilder.Predicate.Like(a1, false, a2, a3);
		}

		static string EscapeLikeText(string text)
		{
			if (text.IndexOfAny(new[] { '%', '_' }) < 0)
				return text;

			var builder = new StringBuilder(text.Length);

			foreach (var ch in text)
			{
				switch (ch)
				{
					case '%':
					case '_':
					case '~':
						builder.Append('~');
						break;
				}

				builder.Append(ch);
			}

			return builder.ToString();
		}

		static Converter<object,object> GetLikeEscaper(string start, string end)
		{
			return value => value == null? null: start + EscapeLikeText(value.ToString()) + end;
		}

		#endregion

		#endregion

		#region Search Condition Parser

		void ParseSearchCondition(ICollection<SqlBuilder.Condition> conditions, QuerySource query, ParseInfo parseInfo)
		{
			switch (parseInfo.NodeType)
			{
				case ExpressionType.AndAlso:
					{
						var pi = parseInfo.Convert<BinaryExpression>();
						var e  = parseInfo.Expr as BinaryExpression;

						ParseSearchCondition(conditions, query, pi.Create(e.Left,  pi.Property(Binary.Left)));
						ParseSearchCondition(conditions, query, pi.Create(e.Right, pi.Property(Binary.Right)));

						return;
					}

				case ExpressionType.OrElse:
					{
						var pi = parseInfo.Convert<BinaryExpression>();
						var e  = parseInfo.Expr as BinaryExpression;

						var orCondition = new SqlBuilder.SearchCondition();

						ParseSearchCondition(orCondition.Conditions, query, pi.Create(e.Left,  pi.Property(Binary.Left)));
						ParseSearchCondition(orCondition.Conditions, query, pi.Create(e.Right, pi.Property(Binary.Right)));

						orCondition.Conditions[0].IsOr = true;

						conditions.Add(new SqlBuilder.Condition(false, orCondition));

						return;
					}

				case ExpressionType.Not:
					{
						var pi = parseInfo.Convert<UnaryExpression>();
						var e  = parseInfo.Expr as UnaryExpression;

						var notCondition = new SqlBuilder.SearchCondition();

						ParseSearchCondition(notCondition.Conditions, query, pi.Create(e.Operand, pi.Property(Unary.Operand)));

						if (notCondition.Conditions.Count == 1 && notCondition.Conditions[0].Predicate is SqlBuilder.Predicate.NotExpr)
						{
							var p = notCondition.Conditions[0].Predicate as SqlBuilder.Predicate.NotExpr;
							p.IsNot = !p.IsNot;
							conditions.Add(notCondition.Conditions[0]);
						}
						else
							conditions.Add(new SqlBuilder.Condition(true, notCondition));

						return;
					}
			}

			var predicate = ParsePredicate(query, parseInfo);
			conditions.Add(new SqlBuilder.Condition(false, predicate));
		}

		#endregion

		#region Helpers

		QuerySource.SubQuery WrapInSubQuery(QuerySource source)
		{
			var result = new QuerySource.SubQuery(new SqlBuilder(), source.SqlBuilder, source);
			CurrentSql = result.SqlBuilder;
			return result;
		}

		SqlFunctionAttribute GetFunctionAttribute(ICustomAttributeProvider member)
		{
			var attrs = member.GetCustomAttributes(typeof(SqlFunctionAttribute), true);

			if (attrs.Length == 0)
				return null;

			SqlFunctionAttribute attr = null;

			foreach (SqlFunctionAttribute a in attrs)
			{
				if (a.SqlProvider == _info.SqlProvider.Name)
				{
					attr = a;
					break;
				}

				if (a.SqlProvider == null)
					attr = a;
			}

			return attr;
		}

		#endregion
	}
}
