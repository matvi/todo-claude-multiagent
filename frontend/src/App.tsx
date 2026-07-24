import { useTodos } from './hooks/useTodos';
import { TodoForm } from './components/TodoForm';
import { TodoList } from './components/TodoList';

export default function App() {
  const { todos, loading, error, create, update, toggleComplete, remove } =
    useTodos();

  return (
    <main className="app">
      <h1 className="app__title">Todos</h1>

      <TodoForm onCreate={create} />

      {error && (
        <p className="app__error" role="alert">
          {error}
        </p>
      )}

      {loading ? (
        <p className="app__loading">Loading…</p>
      ) : (
        <TodoList
          todos={todos}
          onToggleComplete={toggleComplete}
          onUpdate={update}
          onRemove={remove}
        />
      )}
    </main>
  );
}
