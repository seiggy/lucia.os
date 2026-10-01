"use client";

import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { cn } from "@/lib/utils";
import type { DynamicToolUIPart } from "ai";
import { ChevronDownIcon, WrenchIcon } from "lucide-react";
import type { ComponentProps, ReactNode } from "react";

// Adapted from AI Elements' tool: dynamic tools only, and plain text where the registry uses badges and shiki.
export type ToolProps = ComponentProps<typeof Collapsible>;

export const Tool = ({ className, ...props }: ToolProps) => (
  <Collapsible
    className={cn("group not-prose w-full rounded-md border", className)}
    data-slot="tool"
    {...props}
  />
);

export type ToolHeaderProps = Omit<
  ComponentProps<typeof CollapsibleTrigger>,
  "title"
> & {
  title?: ReactNode;
  state: DynamicToolUIPart["state"];
  toolName: string;
  icon?: ReactNode;
  status?: ReactNode;
};

const statusLabels: Record<DynamicToolUIPart["state"], string> = {
  "approval-requested": "Needs your approval",
  "approval-responded": "Answered",
  "input-available": "Running…",
  "input-streaming": "Preparing…",
  "output-available": "Done",
  "output-denied": "Not run",
  "output-error": "Failed",
};

export const ToolHeader = ({
  className,
  title,
  state,
  toolName,
  icon,
  status,
  ...props
}: ToolHeaderProps) => (
  <CollapsibleTrigger
    className={cn("flex w-full items-center gap-2 p-3 text-left", className)}
    data-slot="tool-header"
    {...props}
  >
    {icon ?? <WrenchIcon className="size-4 text-muted-foreground" />}
    <span className="min-w-0 flex-1 font-medium text-sm" data-slot="tool-title">
      {title ?? toolName}
    </span>
    <span
      className="flex shrink-0 items-center gap-1.5 text-muted-foreground text-sm"
      data-slot="tool-status"
    >
      {status ?? statusLabels[state]}
    </span>
    <ChevronDownIcon className="size-4 shrink-0 text-muted-foreground transition-transform group-data-[state=open]:rotate-180" />
  </CollapsibleTrigger>
);

export type ToolContentProps = ComponentProps<typeof CollapsibleContent>;

export const ToolContent = ({ className, ...props }: ToolContentProps) => (
  <CollapsibleContent
    className={cn("space-y-3 px-3 pb-3", className)}
    data-slot="tool-content"
    {...props}
  />
);

const label = (key: string) =>
  key
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .toLowerCase();

const show = (value: unknown) =>
  typeof value === "string" ? value : JSON.stringify(value, null, 2);

export type ToolInputProps = ComponentProps<"dl"> & {
  input: DynamicToolUIPart["input"];
};

export const ToolInput = ({ className, input, ...props }: ToolInputProps) => {
  const entries =
    input && typeof input === "object" && !Array.isArray(input)
      ? Object.entries(input).filter(
          ([, value]) => value !== undefined && value !== null && value !== ""
        )
      : [];
  if (entries.length === 0) {
    return null;
  }

  return (
    <dl className={cn("grid gap-2", className)} data-slot="tool-input" {...props}>
      {entries.map(([key, value]) => (
        <div className="grid gap-0.5" key={key}>
          <dt className="text-muted-foreground text-sm">{label(key)}</dt>
          <dd className="whitespace-pre-wrap font-mono text-sm">
            {typeof value === "string" && value.includes("\n") ? (
              <ol data-slot="tool-input-lines">
                {value.split("\n").map((line, index) => (
                  <li key={index}>{line}</li>
                ))}
              </ol>
            ) : (
              show(value)
            )}
          </dd>
        </div>
      ))}
    </dl>
  );
};

export type ToolOutputProps = ComponentProps<"div"> & {
  output: DynamicToolUIPart["output"];
  errorText: DynamicToolUIPart["errorText"];
};

export const ToolOutput = ({
  className,
  output,
  errorText,
  ...props
}: ToolOutputProps) => {
  if (!errorText && (output === undefined || output === null || output === "")) {
    return null;
  }

  return (
    <div className={cn("grid gap-1", className)} data-slot="tool-output" {...props}>
      <p className="text-muted-foreground text-sm">{errorText ? "Error" : "Result"}</p>
      {errorText ? (
        <p className="text-destructive text-sm">{errorText}</p>
      ) : (
        <pre className="max-h-64 overflow-auto whitespace-pre-wrap font-mono text-sm">
          {show(output)}
        </pre>
      )}
    </div>
  );
};
