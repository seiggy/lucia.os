"use client";

import { cn } from "@/lib/utils";
import type { DynamicToolUIPart } from "ai";
import type { ComponentProps, ReactNode } from "react";
import { createContext, useContext, useMemo } from "react";

// Adapted from AI Elements' confirmation: a labelled group and native buttons instead of shadcn's Alert and Button.
type ToolApproval = DynamicToolUIPart["approval"];

interface ConfirmationContextValue {
  approval: ToolApproval;
  state: DynamicToolUIPart["state"];
}

const ConfirmationContext = createContext<ConfirmationContextValue | null>(
  null
);

const useConfirmation = () => {
  const context = useContext(ConfirmationContext);

  if (!context) {
    throw new Error("Confirmation components must be used within Confirmation");
  }

  return context;
};

export type ConfirmationProps = ComponentProps<"div"> & {
  approval?: ToolApproval;
  state: DynamicToolUIPart["state"];
};

export const Confirmation = ({
  className,
  approval,
  state,
  ...props
}: ConfirmationProps) => {
  const contextValue = useMemo(() => ({ approval, state }), [approval, state]);

  if (!approval || state === "input-streaming" || state === "input-available") {
    return null;
  }

  return (
    <ConfirmationContext.Provider value={contextValue}>
      <div
        className={cn("flex flex-col gap-2 rounded-md border p-3", className)}
        data-slot="confirmation"
        role="group"
        {...props}
      />
    </ConfirmationContext.Provider>
  );
};

export type ConfirmationTitleProps = ComponentProps<"p">;

export const ConfirmationTitle = ({
  className,
  ...props
}: ConfirmationTitleProps) => (
  <p
    className={cn("text-sm", className)}
    data-slot="confirmation-title"
    {...props}
  />
);

export interface ConfirmationRequestProps {
  children?: ReactNode;
}

export const ConfirmationRequest = ({ children }: ConfirmationRequestProps) => {
  const { state } = useConfirmation();

  if (state !== "approval-requested") {
    return null;
  }

  return children;
};

export type ConfirmationActionsProps = ComponentProps<"div">;

export const ConfirmationActions = ({
  className,
  ...props
}: ConfirmationActionsProps) => {
  const { state } = useConfirmation();

  if (state !== "approval-requested") {
    return null;
  }

  return (
    <div
      className={cn("flex flex-wrap items-center gap-2", className)}
      data-slot="confirmation-actions"
      {...props}
    />
  );
};

export type ConfirmationActionProps = ComponentProps<"button">;

export const ConfirmationAction = (props: ConfirmationActionProps) => (
  <button data-slot="confirmation-action" type="button" {...props} />
);
